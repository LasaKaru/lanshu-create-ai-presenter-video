using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Models;

namespace Lanshu.Presenter.Core.Branding;

/// <summary>
/// A reusable look: the colours, type, mark and card copy that make two videos read as coming
/// from the same channel. A kit only carries presentation — never anything about what is said,
/// how long it runs, or which voice speaks it, because those belong to the job, not the brand.
/// </summary>
public sealed class BrandKit
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#F4C430";

    [JsonPropertyName("caption_font")]
    public string CaptionFont { get; set; } = string.Empty;

    /// <summary>classic | karaoke | boxed | tiktok</summary>
    [JsonPropertyName("caption_style")]
    public string CaptionStyle { get; set; } = "classic";

    [JsonPropertyName("watermark")]
    public string Watermark { get; set; } = string.Empty;

    [JsonPropertyName("music_path")]
    public string MusicPath { get; set; } = string.Empty;

    [JsonPropertyName("music_gain_db")]
    public double MusicGainDb { get; set; } = -22;

    [JsonPropertyName("intro")]
    public CardSettings Intro { get; set; } = new();

    [JsonPropertyName("outro")]
    public CardSettings Outro { get; set; } = new();

    public BrandKit Clone() => new()
    {
        Name = Name,
        AccentColor = AccentColor,
        CaptionFont = CaptionFont,
        CaptionStyle = CaptionStyle,
        Watermark = Watermark,
        MusicPath = MusicPath,
        MusicGainDb = MusicGainDb,
        Intro = Intro.Clone(),
        Outro = Outro.Clone(),
    };

    /// <summary>
    /// Writes the kit's look onto a job. Only fields the kit actually carries are applied, so a
    /// kit with no watermark does not erase one the operator set on the job by hand.
    /// </summary>
    public void ApplyTo(JobCreative creative)
    {
        if (!string.IsNullOrWhiteSpace(AccentColor))
        {
            creative.AccentColor = AccentColor;
        }

        if (!string.IsNullOrWhiteSpace(CaptionFont))
        {
            creative.CaptionFont = CaptionFont;
        }

        if (!string.IsNullOrWhiteSpace(CaptionStyle))
        {
            creative.CaptionStyle = CaptionStyle;
        }

        if (!string.IsNullOrWhiteSpace(Watermark))
        {
            creative.Watermark = Watermark;
        }

        if (!string.IsNullOrWhiteSpace(MusicPath))
        {
            creative.MusicPath = MusicPath;
            creative.MusicGainDb = MusicGainDb;
        }

        creative.Intro = Intro.Clone();
        creative.Outro = Outro.Clone();
        creative.BrandKit = Name;
    }
}
