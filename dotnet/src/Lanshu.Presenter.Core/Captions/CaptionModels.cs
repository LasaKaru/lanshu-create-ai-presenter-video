using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Asr;

namespace Lanshu.Presenter.Core.Captions;

public sealed class CaptionPhrase
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("start_s")]
    public double StartSeconds { get; set; }

    [JsonPropertyName("end_s")]
    public double EndSeconds { get; set; }

    [JsonPropertyName("highlight")]
    public string Highlight { get; set; } = string.Empty;

    [JsonPropertyName("words")]
    public List<WordTiming> Words { get; set; } = new();

    [JsonIgnore]
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}

public sealed class KeywordCallout
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("kicker")]
    public string Kicker { get; set; } = string.Empty;

    [JsonPropertyName("start_s")]
    public double StartSeconds { get; set; }

    [JsonPropertyName("end_s")]
    public double EndSeconds { get; set; }

    [JsonPropertyName("preset")]
    public string Preset { get; set; } = "radial_burst";
}

public sealed class CaptionPlan
{
    [JsonPropertyName("phrases")]
    public List<CaptionPhrase> Phrases { get; set; } = new();

    [JsonPropertyName("callouts")]
    public List<KeywordCallout> Callouts { get; set; } = new();

    [JsonPropertyName("word_timings_are_measured")]
    public bool WordTimingsAreMeasured { get; set; }
}

/// <summary>
/// Frame geometry the caption layer needs to keep a phrase inside the safe area. Phrase length
/// is derived from the real font size and usable width rather than a fixed character count,
/// so 9:16 and 21:9 both stay readable without clipping.
/// </summary>
public sealed record CaptionLayout(int Width, int Height)
{
    /// <summary>Horizontal margin on each side, as a fraction of the frame width.</summary>
    public double SideMargin { get; init; } = 0.08;

    /// <summary>Caption font size as a fraction of the frame height.</summary>
    public double FontScale { get; init; } = 0.038;

    /// <summary>Captions may wrap onto this many lines before a phrase is split.</summary>
    public int MaxLines { get; init; } = 2;

    public bool Portrait => Height > Width;

    public int FontSizePixels => Math.Max(18, (int)Math.Round(Height * (Portrait ? FontScale : FontScale * 1.25)));

    public double UsableWidthPixels => Width * (1 - (SideMargin * 2));

    public int MaxCharactersPerPhrase(bool cjk)
    {
        // A latin glyph averages a little over half the nominal size; a CJK glyph fills it.
        var glyphWidth = FontSizePixels * (cjk ? 1.0 : 0.52);
        var perLine = Math.Max(6, (int)Math.Floor(UsableWidthPixels / glyphWidth));
        return Math.Clamp(perLine * MaxLines, 10, 90);
    }
}
