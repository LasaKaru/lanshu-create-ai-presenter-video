using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Environment;

public sealed record DiagnosticsBundleResult(string Path, long Bytes, IReadOnlyList<string> Contents);

/// <summary>
/// Collects everything a bug report needs and nothing it does not.
///
/// The redaction is the whole reason this exists as code rather than as an instruction to zip a
/// folder yourself. Settings sit next to secrets, job records carry provider requests, and a user
/// trying to be helpful will attach all of it to a public issue. So the bundle is built from an
/// explicit list of what to include, every value that could be a credential is replaced before it
/// is written, and the manifest says plainly what was taken and what was left out.
/// </summary>
public static class DiagnosticsBundle
{
    /// <summary>
    /// Words that are never innocent wherever they appear in a key: a field mentioning a password
    /// or a credential is a field whose value does not belong in a bug report.
    /// </summary>
    private static readonly string[] AlwaysSecret =
    {
        "secret", "password", "passphrase", "credential", "authorization", "cookie", "apikey",
    };

    /// <summary>
    /// Words that mean a credential only when they are the whole of a name segment. "key" and
    /// "token" appear inside plenty of harmless settings — max_output_tokens is a number a
    /// maintainer wants to see — so these match per segment rather than as a substring.
    /// </summary>
    private static readonly string[] SecretSegments = { "key", "token", "auth", "bearer" };

    /// <summary>A tail of a log is enough to see a failure; a whole render log is not.</summary>
    private const int LogTailBytes = 256 * 1024;

    public static async Task<DiagnosticsBundleResult> BuildAsync(
        SettingsStore store,
        string outputPath,
        string? jobDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var contents = new List<string>();
        FileSystemUtil.TryDelete(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        await using var stream = File.Create(outputPath);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteAsync(archive, "about.txt", BuildAbout(), contents).ConfigureAwait(false);

            await WriteAsync(archive, "settings.redacted.json", RedactSettings(store), contents)
                .ConfigureAwait(false);

            await WriteAsync(archive, "tools.txt", await ProbeToolsAsync(store, cancellationToken).ConfigureAwait(false), contents)
                .ConfigureAwait(false);

            // Which secrets exist matters for diagnosis; their values never do.
            await WriteAsync(archive, "secrets.names-only.txt", ListSecretNames(store), contents)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(jobDirectory) && Directory.Exists(jobDirectory))
            {
                await AddJobAsync(archive, new JobPaths(jobDirectory), contents, cancellationToken)
                    .ConfigureAwait(false);
            }

            await WriteAsync(archive, "MANIFEST.txt", BuildManifest(contents), contents)
                .ConfigureAwait(false);
        }

        return new DiagnosticsBundleResult(
            Path.GetFullPath(outputPath),
            FileSystemUtil.SafeLength(outputPath),
            contents);
    }

    private static string BuildAbout()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Lanshu AI Presenter Studio diagnostics");
        builder.AppendLine($"version    : {EnvironmentService.AppVersion}");
        builder.AppendLine($"collected  : {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"os         : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        builder.AppendLine($"arch       : {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        builder.AppendLine($"runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        return builder.ToString();
    }

    /// <summary>
    /// Serializes settings with anything credential-shaped replaced. Redaction walks the real JSON
    /// tree rather than matching on known field names, so a setting added later is redacted by
    /// default instead of leaking until somebody remembers to add it to a list.
    /// </summary>
    internal static string RedactSettings(SettingsStore store)
    {
        var node = JsonNode.Parse(JobJson.Serialize(store.Load()));
        Redact(node);
        return node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
               ?? "{}";
    }

    internal static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject item:
            {
                foreach (var pair in item.ToList())
                {
                    if (LooksSecret(pair.Key) && pair.Value is JsonValue)
                    {
                        var text = pair.Value.ToString();
                        item[pair.Key] = string.IsNullOrEmpty(text) ? string.Empty : "[redacted]";
                        continue;
                    }

                    Redact(pair.Value);
                }

                break;
            }

            case JsonArray array:
            {
                foreach (var entry in array)
                {
                    Redact(entry);
                }

                break;
            }
        }
    }

    internal static bool LooksSecret(string key)
    {
        var name = key ?? string.Empty;

        if (AlwaysSecret.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Split on the separators these names actually use, then match a whole segment.
        var segments = name.Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment =>
            SecretSegments.Any(marker => segment.Equals(marker, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ListSecretNames(SettingsStore store)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Credential names that are set. Values are deliberately not included.");
        builder.AppendLine();

        try
        {
            foreach (var name in store.SecretNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                builder.AppendLine($"- {name}");
            }
        }
        catch (Exception exception)
        {
            builder.AppendLine($"(could not be read: {exception.Message})");
        }

        return builder.ToString();
    }

    private static async Task<string> ProbeToolsAsync(SettingsStore store, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var settings = store.Load();

        try
        {
            var toolset = await MediaToolset
                .ResolveAsync(settings.FfmpegPath, settings.FfprobePath, cancellationToken)
                .ConfigureAwait(false);

            builder.AppendLine($"ffmpeg  : {toolset.FfmpegPath}");
            builder.AppendLine($"ffprobe : {toolset.FfprobePath}");

            var version = await ProcessRunner
                .RunAsync(toolset.FfmpegPath, new[] { "-hide_banner", "-version" }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            builder.AppendLine();
            builder.AppendLine(ProcessResult.Tail(version.Combined, 6));
        }
        catch (Exception exception)
        {
            builder.AppendLine($"ffmpeg could not be resolved: {exception.Message}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adds one job's records: the manifest, the QA reports and the tail of the run log. The
    /// rendered video is deliberately not included — it is the large half of the folder and a
    /// maintainer reading a stack trace does not need it.
    /// </summary>
    private static async Task AddJobAsync(
        ZipArchive archive,
        JobPaths paths,
        List<string> contents,
        CancellationToken cancellationToken)
    {
        if (File.Exists(paths.ManifestFile))
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(paths.ManifestFile, cancellationToken).ConfigureAwait(false));
            Redact(node);
            await WriteAsync(archive, "job/job.redacted.json", node?.ToJsonString() ?? "{}", contents)
                .ConfigureAwait(false);
        }

        foreach (var report in SafeFiles(paths.QaReports, "*.md").Take(8))
        {
            await WriteAsync(
                    archive,
                    "job/qa/" + Path.GetFileName(report),
                    await File.ReadAllTextAsync(report, cancellationToken).ConfigureAwait(false),
                    contents)
                .ConfigureAwait(false);
        }

        if (File.Exists(paths.RunLog))
        {
            await WriteAsync(archive, "job/run-log.tail.txt", ReadTail(paths.RunLog, LogTailBytes), contents)
                .ConfigureAwait(false);
        }

        foreach (var doc in SafeFiles(paths.Docs, "*.md").Take(4))
        {
            await WriteAsync(
                    archive,
                    "job/docs/" + Path.GetFileName(doc),
                    await File.ReadAllTextAsync(doc, cancellationToken).ConfigureAwait(false),
                    contents)
                .ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> SafeFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            return Directory.EnumerateFiles(directory, pattern);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Reads the last <paramref name="bytes"/> of a file, starting on a line boundary.</summary>
    internal static string ReadTail(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > bytes)
            {
                stream.Seek(-bytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            // A tail that starts mid-line reads as corruption; drop the partial first line.
            var newline = text.IndexOf('\n');
            return stream.Length > bytes && newline >= 0 ? text[(newline + 1)..] : text;
        }
        catch (IOException exception)
        {
            return $"(the log could not be read: {exception.Message})";
        }
    }

    private static string BuildManifest(IReadOnlyList<string> contents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("What is in this bundle");
        builder.AppendLine();
        foreach (var entry in contents)
        {
            builder.AppendLine($"- {entry}");
        }

        builder.AppendLine();
        builder.AppendLine("What is deliberately NOT in it");
        builder.AppendLine();
        builder.AppendLine("- API keys and tokens. Settings and the job record are included with every");
        builder.AppendLine("  credential-shaped value replaced by [redacted]; only the names are listed.");
        builder.AppendLine("- Rendered video, audio and images. They are the large half of a job folder");
        builder.AppendLine("  and are not needed to read a stack trace.");
        builder.AppendLine("- Anything outside the settings directory and the one job you named.");
        builder.AppendLine();
        builder.AppendLine("Read it before you attach it to a public issue. It is your machine, not ours.");
        return builder.ToString();
    }

    private static async Task WriteAsync(ZipArchive archive, string name, string content, List<string> contents)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        await writer.WriteAsync(content).ConfigureAwait(false);
        contents.Add(name);
    }
}
