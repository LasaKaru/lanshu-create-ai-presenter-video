using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Lanshu.Presenter.Core.Timeline;

public enum ClipKind
{
    Presenter,
    Insert,
}

/// <summary>
/// One clip on the timeline. Authored start, authored duration and source offset are kept
/// independent so an opening can change length without disturbing presenter source timing.
/// </summary>
public sealed class TimelineClip
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "presenter";

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("is_still")]
    public bool IsStill { get; set; }

    [JsonPropertyName("authored_start_s")]
    public double AuthoredStartSeconds { get; set; }

    [JsonPropertyName("authored_duration_s")]
    public double AuthoredDurationSeconds { get; set; }

    [JsonPropertyName("source_offset_s")]
    public double SourceOffsetSeconds { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonIgnore]
    public double AuthoredEndSeconds => AuthoredStartSeconds + AuthoredDurationSeconds;

    [JsonIgnore]
    public ClipKind KindValue => Kind.Equals("insert", StringComparison.OrdinalIgnoreCase)
        ? ClipKind.Insert
        : ClipKind.Presenter;
}

public sealed class RenderTimeline
{
    [JsonPropertyName("duration_s")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    [JsonPropertyName("narration")]
    public string NarrationPath { get; set; } = string.Empty;

    [JsonPropertyName("music")]
    public string MusicPath { get; set; } = string.Empty;

    [JsonPropertyName("music_gain_db")]
    public double MusicGainDb { get; set; } = -22;

    [JsonPropertyName("subtitles")]
    public string SubtitlePath { get; set; } = string.Empty;

    [JsonPropertyName("fonts_directory")]
    public string FontsDirectory { get; set; } = string.Empty;

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#F4C430";

    [JsonPropertyName("progress_bar")]
    public bool ProgressBar { get; set; } = true;

    [JsonPropertyName("clips")]
    public List<TimelineClip> Clips { get; set; } = new();

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Timeline");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- duration: `{DurationSeconds:0.000}s`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- format: `{Width}x{Height} @ {Fps}fps`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- program clock: `{Path.GetFileName(NarrationPath)}`");
        builder.AppendLine();
        builder.AppendLine("| Track | Start | Duration | Source offset | Source |");
        builder.AppendLine("|-------|-------|----------|---------------|--------|");

        foreach (var clip in Clips)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {clip.Kind} | {clip.AuthoredStartSeconds:0.000}s | {clip.AuthoredDurationSeconds:0.000}s | {clip.SourceOffsetSeconds:0.000}s | {Path.GetFileName(clip.Source)} |");
        }

        builder.AppendLine();
        builder.AppendLine("All video sources are muted. Only the approved narration and any intentional mix track are routed to the program.");
        builder.AppendLine();
        return builder.ToString();
    }
}
