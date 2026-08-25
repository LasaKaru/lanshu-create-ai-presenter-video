using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lanshu.Presenter.Core.Preflight;

public sealed class PreflightReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("remote_ready")]
    public bool RemoteReady { get; set; }

    [JsonPropertyName("job")]
    public string Job { get; set; } = "job.json";

    [JsonPropertyName("checked_utc")]
    public string CheckedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("errors")]
    public List<string> Errors { get; set; } = new();

    [JsonPropertyName("remote_blockers")]
    public List<string> RemoteBlockers { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("media")]
    public JsonObject Media { get; set; } = new();
}
