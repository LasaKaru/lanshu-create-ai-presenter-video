using System.Text.Json.Serialization;

namespace Lanshu.Presenter.Core.Publishing;

public sealed class PublishException : Exception
{
    public PublishException(string message) : base(message) { }
}

public enum PublishOutcome
{
    /// <summary>The operator has not approved this specific upload yet.</summary>
    NeedsApproval,

    /// <summary>Nothing is configured to publish to.</summary>
    NotConfigured,

    /// <summary>The plan was printed and nothing was sent, because a dry run was asked for.</summary>
    DryRun,

    Published,

    Failed,
}

/// <summary>
/// Exactly what would leave this machine, itemised before anything does. This is the same shape
/// of disclosure the paid-generation gate makes: an upload is irreversible in a way a render is
/// not, so the operator sees the destination, the visibility and the file sizes first.
/// </summary>
public sealed class PublishPlan
{
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = string.Empty;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    /// <summary>private | unlisted | public — defaulting to private is the only safe default.</summary>
    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "private";

    [JsonPropertyName("video_path")]
    public string VideoPath { get; set; } = string.Empty;

    [JsonPropertyName("video_bytes")]
    public long VideoBytes { get; set; }

    [JsonPropertyName("thumbnail_path")]
    public string ThumbnailPath { get; set; } = string.Empty;

    [JsonPropertyName("caption_paths")]
    public List<string> CaptionPaths { get; set; } = new();

    public IEnumerable<string> Describe()
    {
        yield return $"destination : {Destination}";
        yield return $"endpoint    : {Endpoint}";
        yield return $"title       : {Title}";
        yield return $"visibility  : {Visibility}";
        yield return $"video       : {Path.GetFileName(VideoPath)} ({VideoBytes / 1024 / 1024} MB)";

        if (!string.IsNullOrWhiteSpace(ThumbnailPath))
        {
            yield return $"thumbnail   : {Path.GetFileName(ThumbnailPath)}";
        }

        if (CaptionPaths.Count > 0)
        {
            yield return $"captions    : {string.Join(", ", CaptionPaths.Select(Path.GetFileName))}";
        }

        if (Tags.Count > 0)
        {
            yield return $"tags        : {string.Join(", ", Tags)}";
        }
    }
}

public sealed record PublishResult(
    PublishOutcome Outcome,
    string Message,
    string RemoteId = "",
    string RemoteUrl = "");
