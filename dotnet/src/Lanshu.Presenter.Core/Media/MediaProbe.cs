using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lanshu.Presenter.Core.Media;

/// <summary>Typed view over the ffprobe JSON the pipeline actually reads.</summary>
public sealed class MediaProbe
{
    [JsonPropertyName("streams")]
    public List<MediaStream> Streams { get; set; } = new();

    [JsonPropertyName("format")]
    public MediaFormat Format { get; set; } = new();

    [JsonIgnore]
    public JsonNode? Raw { get; set; }

    public MediaStream? Video => Streams.FirstOrDefault(stream => stream.CodecType == "video");

    public MediaStream? Audio => Streams.FirstOrDefault(stream => stream.CodecType == "audio");

    public bool HasVideo => Video is not null;

    public bool HasAudio => Audio is not null;

    public double DurationSeconds => Format.DurationSeconds
        ?? Video?.DurationSeconds
        ?? Audio?.DurationSeconds
        ?? 0;

    /// <summary>Strips the directory from format.filename so reports stay portable.</summary>
    public JsonNode? PortableRaw()
    {
        if (Raw is null)
        {
            return null;
        }

        var clone = JsonNode.Parse(Raw.ToJsonString());
        if (clone?["format"]?["filename"] is JsonValue value && value.TryGetValue<string>(out var filename))
        {
            clone!["format"]!["filename"] = Path.GetFileName(filename.Replace('\\', '/'));
        }

        return clone;
    }
}

public sealed class MediaStream
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("codec_type")]
    public string CodecType { get; set; } = string.Empty;

    [JsonPropertyName("codec_name")]
    public string CodecName { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("pix_fmt")]
    public string PixelFormat { get; set; } = string.Empty;

    [JsonPropertyName("sample_rate")]
    public string SampleRate { get; set; } = string.Empty;

    [JsonPropertyName("channels")]
    public int Channels { get; set; }

    [JsonPropertyName("r_frame_rate")]
    public string RFrameRate { get; set; } = string.Empty;

    [JsonPropertyName("avg_frame_rate")]
    public string AvgFrameRate { get; set; } = string.Empty;

    [JsonPropertyName("nb_frames")]
    public string NumberOfFrames { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    public string Duration { get; set; } = string.Empty;

    public double? DurationSeconds => ParseDouble(Duration);

    public double FrameRate => ParseRational(AvgFrameRate) is { } average && average > 0
        ? average
        : ParseRational(RFrameRate) ?? 0;

    public static double? ParseRational(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return ParseDouble(value);
    }

    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}

public sealed class MediaFormat
{
    [JsonPropertyName("filename")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("format_name")]
    public string FormatName { get; set; } = string.Empty;

    [JsonPropertyName("duration")]
    public string Duration { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public string Size { get; set; } = string.Empty;

    [JsonPropertyName("bit_rate")]
    public string BitRate { get; set; } = string.Empty;

    public double? DurationSeconds =>
        double.TryParse(Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
