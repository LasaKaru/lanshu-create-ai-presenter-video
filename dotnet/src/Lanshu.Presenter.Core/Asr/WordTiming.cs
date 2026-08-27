using System.Text.Json.Serialization;

namespace Lanshu.Presenter.Core.Asr;

public sealed class WordTiming
{
    [JsonPropertyName("word")]
    public string Word { get; set; } = string.Empty;

    [JsonPropertyName("start_s")]
    public double StartSeconds { get; set; }

    [JsonPropertyName("end_s")]
    public double EndSeconds { get; set; }

    [JsonIgnore]
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}

public sealed class AsrResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("words")]
    public List<WordTiming> Words { get; set; } = new();

    [JsonPropertyName("word_timings_are_measured")]
    public bool WordTimingsAreMeasured { get; set; }
}

public interface IAsrProvider
{
    string Provider { get; }

    string Model { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<AsrResult> TranscribeAsync(
        string audioPath,
        string language,
        CancellationToken cancellationToken = default);
}
