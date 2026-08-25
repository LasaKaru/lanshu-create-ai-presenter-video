using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lanshu.Presenter.Core.Delivery;

public sealed class DeliveryReport
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "verified";

    [JsonPropertyName("finalized_utc")]
    public string FinalizedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    [JsonPropertyName("master")]
    public string Master { get; set; } = string.Empty;

    [JsonPropertyName("share")]
    public string Share { get; set; } = string.Empty;

    [JsonPropertyName("contact_sheet")]
    public string ContactSheet { get; set; } = string.Empty;

    [JsonPropertyName("cover_frame")]
    public string CoverFrame { get; set; } = string.Empty;

    [JsonPropertyName("captions_srt")]
    public string CaptionsSrt { get; set; } = string.Empty;

    [JsonPropertyName("duration_s")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("target_program_lufs")]
    public double TargetProgramLufs { get; set; } = -16;

    [JsonPropertyName("source_loudness")]
    public JsonObject SourceLoudness { get; set; } = new();

    [JsonPropertyName("source_probe")]
    public JsonNode? SourceProbe { get; set; }

    [JsonPropertyName("master_probe")]
    public JsonNode? MasterProbe { get; set; }

    [JsonPropertyName("share_probe")]
    public JsonNode? ShareProbe { get; set; }

    [JsonPropertyName("full_decode_passed")]
    public bool FullDecodePassed { get; set; }

    [JsonPropertyName("black_frame_events")]
    public int BlackFrameEvents { get; set; }

    [JsonPropertyName("master_bytes")]
    public long MasterBytes { get; set; }

    [JsonPropertyName("share_bytes")]
    public long ShareBytes { get; set; }
}
