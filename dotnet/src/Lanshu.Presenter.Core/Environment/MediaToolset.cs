using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Environment;

/// <summary>Resolved ffmpeg/ffprobe pair plus the encoder and filter capabilities the pipeline needs.</summary>
public sealed class MediaToolset
{
    public required string FfmpegPath { get; init; }

    public required string FfprobePath { get; init; }

    public string Version { get; init; } = string.Empty;

    public IReadOnlySet<string> Filters { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> Encoders { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool HasSubtitleBurnIn => Filters.Contains("subtitles") || Filters.Contains("ass");

    public bool HasDrawText => Filters.Contains("drawtext");

    public bool HasZoomPan => Filters.Contains("zoompan");

    public bool HasLibx264 => Encoders.Contains("libx264");

    public bool HasAac => Encoders.Contains("aac");

    public bool HasLoudnorm => Filters.Contains("loudnorm");

    public IReadOnlyList<string> MissingRequirements()
    {
        var missing = new List<string>();
        if (!HasLibx264)
        {
            missing.Add("libx264 encoder");
        }

        if (!HasAac)
        {
            missing.Add("aac encoder");
        }

        if (!HasLoudnorm)
        {
            missing.Add("loudnorm filter");
        }

        return missing;
    }

    public static async Task<MediaToolset> ResolveAsync(
        string? ffmpegOverride = null,
        string? ffprobeOverride = null,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = ToolLocator.Find("ffmpeg", ffmpegOverride)
            ?? throw new ExternalToolException(
                "ffmpeg was not found. Run 'lanshu doctor --install' or set the FFmpeg path in Settings.");
        var ffprobe = ToolLocator.Find("ffprobe", ffprobeOverride)
            ?? ProbeSibling(ffmpeg)
            ?? throw new ExternalToolException(
                "ffprobe was not found next to ffmpeg. Install the full FFmpeg package, not just the ffmpeg binary.");

        var version = string.Empty;
        var filters = new HashSet<string>(StringComparer.Ordinal);
        var encoders = new HashSet<string>(StringComparer.Ordinal);

        var versionResult = await ProcessRunner
            .RunAsync(ffmpeg, new[] { "-hide_banner", "-version" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (versionResult.Success)
        {
            version = versionResult.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? string.Empty;
        }

        var filterResult = await ProcessRunner
            .RunAsync(ffmpeg, new[] { "-hide_banner", "-filters" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        foreach (var name in ParseSecondColumn(filterResult.StandardOutput))
        {
            filters.Add(name);
        }

        var encoderResult = await ProcessRunner
            .RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        foreach (var name in ParseSecondColumn(encoderResult.StandardOutput))
        {
            encoders.Add(name);
        }

        return new MediaToolset
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ffprobe,
            Version = version,
            Filters = filters,
            Encoders = encoders,
        };
    }

    private static string? ProbeSibling(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var candidate = Path.Combine(directory, ToolLocator.ExecutableName("ffprobe"));
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Both '-filters' and '-encoders' print a flag column then the name, e.g. " V..... zoompan  Apply Zoom...".
    /// </summary>
    private static IEnumerable<string> ParseSecondColumn(string output)
    {
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length < 3 || !line.StartsWith(" ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[1];
            if (name.Length == 0 || name is "=" or "------")
            {
                continue;
            }

            yield return name;
        }
    }
}

public sealed class EnvironmentReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("ffmpeg_path")]
    public string FfmpegPath { get; set; } = string.Empty;

    [JsonPropertyName("ffprobe_path")]
    public string FfprobePath { get; set; } = string.Empty;

    [JsonPropertyName("ffmpeg_version")]
    public string FfmpegVersion { get; set; } = string.Empty;

    [JsonPropertyName("subtitle_burn_in")]
    public bool SubtitleBurnIn { get; set; }

    [JsonPropertyName("drawtext")]
    public bool DrawText { get; set; }

    [JsonPropertyName("zoompan")]
    public bool ZoomPan { get; set; }

    [JsonPropertyName("libx264")]
    public bool Libx264 { get; set; }

    [JsonPropertyName("aac")]
    public bool Aac { get; set; }

    [JsonPropertyName("loudnorm")]
    public bool Loudnorm { get; set; }

    [JsonPropertyName("video_encoder")]
    public string VideoEncoder { get; set; } = string.Empty;

    [JsonPropertyName("hardware_encoders")]
    public List<string> HardwareEncoders { get; set; } = new();

    [JsonPropertyName("offline_voices")]
    public List<string> OfflineVoices { get; set; } = new();

    [JsonPropertyName("configured_providers")]
    public List<string> ConfiguredProviders { get; set; } = new();

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = string.Empty;

    [JsonPropertyName("problems")]
    public List<string> Problems { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}
