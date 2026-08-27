using System.Text;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Publishing;

/// <summary>
/// Uploads a finished video to whatever destination the operator configured.
///
/// Publishing is the one action in this app that cannot be undone from here. A render can be
/// thrown away and a paid call is at least only money; a video that goes out under someone's
/// name has been seen. So this follows the same shape as the paid-generation gate — state the
/// whole plan, refuse to move until *that* plan is approved, and default the visibility to
/// private so the failure mode of a mistake is an unlisted draft rather than a publication.
/// </summary>
public sealed class PublishService
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;

    public PublishService(AppSettings settings, SettingsStore store, HttpClient httpClient, Action<string>? log = null)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
        _log = log;
    }

    /// <summary>
    /// Describes exactly what would be uploaded. Building the plan never sends anything, so it is
    /// safe to call for a preview and is what the approval prompt is built from.
    /// </summary>
    public PublishPlan BuildPlan(JobPaths paths, JobManifest job, string? visibilityOverride = null)
    {
        var publish = _settings.Publish;
        var master = paths.Resolve(job.Artifacts.Master);

        var plan = new PublishPlan
        {
            Destination = string.IsNullOrWhiteSpace(publish.Destination)
                ? "(unnamed destination)"
                : publish.Destination,
            Endpoint = publish.UploadUrl,
            Title = ResolveTitle(job),
            Description = ReadIfPresent(paths, "-description.txt"),
            Tags = BuildTags(job),
            Visibility = string.IsNullOrWhiteSpace(visibilityOverride)
                ? (string.IsNullOrWhiteSpace(publish.DefaultVisibility) ? "private" : publish.DefaultVisibility)
                : visibilityOverride,
            VideoPath = master,
            VideoBytes = FileSystemUtil.SafeLength(master),
            ThumbnailPath = job.Artifacts.Thumbnails.Count > 0
                ? paths.Resolve(job.Artifacts.Thumbnails[0])
                : string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(job.Artifacts.CaptionSrt))
        {
            plan.CaptionPaths.Add(paths.Resolve(job.Artifacts.CaptionSrt));
        }

        plan.CaptionPaths.AddRange(job.Artifacts.TranslatedSubtitles.Select(paths.Resolve));

        return plan;
    }

    public async Task<PublishResult> PublishAsync(
        JobPaths paths,
        JobManifest job,
        bool dryRun,
        string? visibilityOverride = null,
        CancellationToken cancellationToken = default)
    {
        var publish = _settings.Publish;

        if (!publish.Enabled || string.IsNullOrWhiteSpace(publish.UploadUrl))
        {
            return new PublishResult(
                PublishOutcome.NotConfigured,
                "no publish destination is configured; set one in Settings before publishing");
        }

        var plan = BuildPlan(paths, job, visibilityOverride);

        if (!File.Exists(plan.VideoPath) || plan.VideoBytes == 0)
        {
            return new PublishResult(
                PublishOutcome.Failed,
                "there is no delivered master to publish; run the job to completion first");
        }

        if (dryRun)
        {
            return new PublishResult(PublishOutcome.DryRun, "nothing was uploaded (dry run)");
        }

        // The approval has to name this exact plan. An approval recorded against a different
        // title, destination or visibility is not an approval of what is about to be sent.
        var fingerprint = Fingerprint(plan);
        if (!string.Equals(job.Plan.PublishApprovedFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new PublishResult(
                PublishOutcome.NeedsApproval,
                "this upload has not been approved yet");
        }

        var token = string.IsNullOrWhiteSpace(publish.SecretKey)
            ? string.Empty
            : _store.GetSecret(publish.SecretKey);

        if (string.IsNullOrWhiteSpace(token))
        {
            return new PublishResult(
                PublishOutcome.Failed,
                $"no credential found; set '{publish.SecretKey}' in secrets.json");
        }

        try
        {
            _log?.Invoke($"Uploading {Path.GetFileName(plan.VideoPath)} to {plan.Destination} as {plan.Visibility}");

            using var content = new MultipartFormDataContent();

            var metadata = BuildMetadata(publish.MetadataTemplate, plan);
            if (!string.IsNullOrWhiteSpace(metadata))
            {
                var part = new StringContent(metadata, Encoding.UTF8, "application/json");
                SetDisposition(part, string.IsNullOrWhiteSpace(publish.MetadataField) ? "metadata" : publish.MetadataField);
                content.Add(part);
            }

            await using var stream = File.OpenRead(plan.VideoPath);
            var file = new StreamContent(stream);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            SetDisposition(
                file,
                string.IsNullOrWhiteSpace(publish.FileField) ? "file" : publish.FileField,
                Path.GetFileName(plan.VideoPath));
            content.Add(file);

            using var request = new HttpRequestMessage(HttpMethod.Post, publish.UploadUrl) { Content = content };
            var header = string.IsNullOrWhiteSpace(publish.AuthHeader) ? "Authorization" : publish.AuthHeader;
            request.Headers.TryAddWithoutValidation(
                header,
                string.IsNullOrWhiteSpace(publish.AuthScheme) ? token : $"{publish.AuthScheme} {token}");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new PublishResult(
                    PublishOutcome.Failed,
                    $"the destination rejected the upload ({(int)response.StatusCode}): {TextUtil.Truncate(body, 300)}");
            }

            var id = ReadPath(body, publish.IdPath);
            var url = string.IsNullOrWhiteSpace(publish.UrlTemplate) || string.IsNullOrWhiteSpace(id)
                ? string.Empty
                : publish.UrlTemplate.Replace("{{ID}}", id, StringComparison.Ordinal);

            return new PublishResult(
                PublishOutcome.Published,
                $"published to {plan.Destination} as {plan.Visibility}",
                id,
                url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PublishResult(PublishOutcome.Failed, exception.Message);
        }
    }

    /// <summary>
    /// Names a multipart section the way RFC 7578 asks for it, with the name and filename in
    /// quotes.
    ///
    /// MultipartFormDataContent's own Add(content, name) writes them bare — `name=file` rather
    /// than `name="file"` — which is legal enough that a lenient server accepts it and strict
    /// ones do not. Since the whole point here is uploading to a platform this code has never
    /// seen, the conservative spelling is the right one.
    /// </summary>
    private static void SetDisposition(HttpContent content, string name, string? fileName = null)
    {
        var disposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
        {
            Name = "\"" + name + "\"",
        };

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            disposition.FileName = "\"" + fileName + "\"";
        }

        content.Headers.ContentDisposition = disposition;
    }

    /// <summary>
    /// Identifies one specific upload. Changing the title, destination, visibility or the file
    /// itself produces a different fingerprint, which retires the old approval — so an approval
    /// can never silently carry over to a publication the operator did not read.
    /// </summary>
    public static string Fingerprint(PublishPlan plan)
    {
        var material = string.Join(
            "|",
            plan.Destination,
            plan.Endpoint,
            plan.Title,
            plan.Visibility,
            Path.GetFileName(plan.VideoPath),
            plan.VideoBytes.ToString());

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    internal static string BuildMetadata(string template, PublishPlan plan)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            // A destination with no template still gets the basics rather than nothing.
            return new JsonObject
            {
                ["title"] = plan.Title,
                ["description"] = plan.Description,
                ["visibility"] = plan.Visibility,
                ["tags"] = new JsonArray(plan.Tags.Select(tag => (JsonNode)tag!).ToArray()),
            }.ToJsonString();
        }

        var tags = string.Join(",", plan.Tags.Select(tag => "\"" + JsonEscape(tag) + "\""));
        return template
            .Replace("{{TITLE}}", JsonEscape(plan.Title), StringComparison.Ordinal)
            .Replace("{{DESCRIPTION}}", JsonEscape(plan.Description), StringComparison.Ordinal)
            .Replace("{{VISIBILITY}}", JsonEscape(plan.Visibility), StringComparison.Ordinal)
            .Replace("{{TAGS}}", tags, StringComparison.Ordinal);
    }

    private static string JsonEscape(string value) => (value ?? string.Empty)
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\r", string.Empty);

    private static string ResolveTitle(JobManifest job)
    {
        var chapter = job.Plan.Chapters.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(chapter?.Title)
               && !chapter.Title.StartsWith("Section ", StringComparison.OrdinalIgnoreCase)
            ? chapter.Title
            : job.JobId;
    }

    private static List<string> BuildTags(JobManifest job) => job.Plan.Chapters
        .Select(chapter => chapter.Keyword)
        .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
        .Select(keyword => keyword.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(12)
        .ToList();

    private static string ReadIfPresent(JobPaths paths, string suffix)
    {
        try
        {
            var match = Directory.Exists(paths.Outputs)
                ? Directory.EnumerateFiles(paths.Outputs, "*" + suffix).FirstOrDefault()
                : null;

            return match is null ? string.Empty : File.ReadAllText(match).Trim();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    internal static string ReadPath(string body, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(body);
            foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                node = node?[part];
            }

            return node?.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
