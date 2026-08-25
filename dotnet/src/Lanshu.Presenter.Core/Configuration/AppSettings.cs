using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Configuration;

/// <summary>
/// User-level configuration stored in the per-user data directory. Credentials live in a
/// sibling secrets file that is never written into a job directory or a report.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = string.Empty;

    [JsonPropertyName("ffmpeg_path")]
    public string FfmpegPath { get; set; } = string.Empty;

    [JsonPropertyName("ffprobe_path")]
    public string FfprobePath { get; set; } = string.Empty;

    [JsonPropertyName("ffmpeg_download_url")]
    public string FfmpegDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("caption_font")]
    public string CaptionFont { get; set; } = string.Empty;

    [JsonPropertyName("fonts_directory")]
    public string FontsDirectory { get; set; } = string.Empty;

    [JsonPropertyName("script")]
    public ScriptSettings Script { get; set; } = new();

    [JsonPropertyName("voice")]
    public VoiceSettings Voice { get; set; } = new();

    [JsonPropertyName("presenter")]
    public PresenterSettings Presenter { get; set; } = new();

    [JsonPropertyName("asr")]
    public AsrSettings Asr { get; set; } = new();

    [JsonPropertyName("defaults")]
    public CreativeDefaults Defaults { get; set; } = new();

    [JsonPropertyName("render")]
    public RenderSettings Render { get; set; } = new();

    [JsonIgnore]
    public string ResolvedWorkspace =>
        string.IsNullOrWhiteSpace(Workspace) ? ToolLocator.DefaultWorkspace : FileSystemUtil.ExpandPath(Workspace);
}

public sealed class RenderSettings
{
    /// <summary>auto | software | nvenc | qsv | videotoolbox | amf</summary>
    [JsonPropertyName("encoder")]
    public string Encoder { get; set; } = "auto";

    /// <summary>Short edge of the fast proxy preview, in pixels.</summary>
    [JsonPropertyName("preview_height")]
    public int PreviewHeight { get; set; } = 640;
}

public sealed class ScriptSettings
{
    /// <summary>auto | anthropic | openai | none. "none" keeps everything on the built-in outline writer.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "auto";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "claude-opus-5";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; set; } = 4000;

    /// <summary>Pause a run after drafting so the narration can be read and edited.</summary>
    [JsonPropertyName("review_before_audio")]
    public bool ReviewBeforeAudio { get; set; }
}

public sealed class VoiceSettings
{
    /// <summary>auto | system | elevenlabs | openai | azure | piper | none</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "auto";

    [JsonPropertyName("voice_id")]
    public string VoiceId { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("rate")]
    public double Rate { get; set; } = 1.0;

    [JsonPropertyName("piper_model_path")]
    public string PiperModelPath { get; set; } = string.Empty;
}

public sealed class PresenterSettings
{
    /// <summary>auto | motion | remote. "motion" is the fully local animated-plate renderer.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "auto";

    [JsonPropertyName("remote")]
    public RemoteJobSettings Remote { get; set; } = new();

    [JsonPropertyName("motion")]
    public MotionPlateSettings Motion { get; set; } = new();

    [JsonPropertyName("lipsync")]
    public RemoteJobSettings LipSync { get; set; } = new();
}

/// <summary>
/// Provider-neutral description of a talking-head or lip-sync HTTP API: submit a job,
/// poll it, then download the result. Field names are configurable so no vendor is baked in.
/// </summary>
public sealed class RemoteJobSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("submit_url")]
    public string SubmitUrl { get; set; } = string.Empty;

    [JsonPropertyName("status_url_template")]
    public string StatusUrlTemplate { get; set; } = string.Empty;

    [JsonPropertyName("auth_header")]
    public string AuthHeader { get; set; } = "Authorization";

    [JsonPropertyName("auth_scheme")]
    public string AuthScheme { get; set; } = "Bearer";

    [JsonPropertyName("secret_key")]
    public string SecretKey { get; set; } = string.Empty;

    [JsonPropertyName("request_template")]
    public string RequestTemplate { get; set; } = string.Empty;

    [JsonPropertyName("upload_mode")]
    public string UploadMode { get; set; } = "data_uri";

    [JsonPropertyName("task_id_path")]
    public string TaskIdPath { get; set; } = "id";

    [JsonPropertyName("status_path")]
    public string StatusPath { get; set; } = "status";

    [JsonPropertyName("succeeded_values")]
    public List<string> SucceededValues { get; set; } = new() { "succeeded", "success", "completed", "done" };

    [JsonPropertyName("failed_values")]
    public List<string> FailedValues { get; set; } = new() { "failed", "error", "canceled", "cancelled" };

    [JsonPropertyName("result_url_path")]
    public string ResultUrlPath { get; set; } = "output.video_url";

    [JsonPropertyName("poll_interval_s")]
    public double PollIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("timeout_s")]
    public double TimeoutSeconds { get; set; } = 1800;

    [JsonPropertyName("price_per_second")]
    public double? PricePerSecond { get; set; }

    [JsonPropertyName("price_evidence_date")]
    public string PriceEvidenceDate { get; set; } = string.Empty;

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(SubmitUrl) && !string.IsNullOrWhiteSpace(RequestTemplate);
}

public sealed class MotionPlateSettings
{
    [JsonPropertyName("zoom_percent")]
    public double ZoomPercent { get; set; } = 6;

    [JsonPropertyName("sway_pixels")]
    public double SwayPixels { get; set; } = 10;

    [JsonPropertyName("breath_period_s")]
    public double BreathPeriodSeconds { get; set; } = 5.5;

    [JsonPropertyName("background")]
    public string Background { get; set; } = "blurred";

    [JsonPropertyName("vignette")]
    public bool Vignette { get; set; } = true;

    [JsonPropertyName("grade")]
    public bool Grade { get; set; } = true;
}

public sealed class AsrSettings
{
    /// <summary>auto | openai | whisper-cli | none. "none" uses the proportional aligner.</summary>
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "auto";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "whisper-1";

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("whisper_cli_path")]
    public string WhisperCliPath { get; set; } = string.Empty;

    [JsonPropertyName("whisper_model_path")]
    public string WhisperModelPath { get; set; } = string.Empty;
}

public sealed class CreativeDefaults
{
    [JsonPropertyName("aspect")]
    public string Aspect { get; set; } = "9:16";

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 30;

    [JsonPropertyName("duration_target_s")]
    public double DurationTargetSeconds { get; set; } = 60;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#F4C430";

    [JsonPropertyName("program_lufs")]
    public double ProgramLufs { get; set; } = -16;

    [JsonPropertyName("segment_lufs")]
    public double SegmentLufs { get; set; } = -17;

    [JsonPropertyName("style")]
    public string Style { get; set; } = "credible contemporary presenter";

    public JobCreative ToCreative() => new()
    {
        Aspect = Aspect,
        Fps = Fps,
        DurationTargetSeconds = DurationTargetSeconds,
        Language = Language,
        AccentColor = AccentColor,
        Style = Style,
    };
}
