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

    /// <summary>Reusable looks, applied to a job by name.</summary>
    [JsonPropertyName("brand_kits")]
    public List<Branding.BrandKit> BrandKits { get; set; } = new();

    [JsonPropertyName("publish")]
    public PublishSettings Publish { get; set; } = new();

    /// <summary>Ask GitHub whether a newer release exists. Notification only; nothing downloads.</summary>
    [JsonPropertyName("check_for_updates")]
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Where to ask. Configurable because this is open source: a fork's users should be told
    /// about the fork's releases, not this repository's.
    /// </summary>
    [JsonPropertyName("update_feed_url")]
    public string UpdateFeedUrl { get; set; } =
        "https://api.github.com/repos/LasaKaru/lanshu-create-ai-presenter-video/releases/latest";

    /// <summary>UI language for the studio: auto | en | zh.</summary>
    [JsonPropertyName("ui_language")]
    public string UiLanguage { get; set; } = "auto";

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

    /// <summary>A lip-sync API reached over HTTP.</summary>
    [JsonPropertyName("lipsync")]
    public RemoteJobSettings LipSync { get; set; } = new();

    /// <summary>A lip-sync tool installed on this machine, run as a subprocess.</summary>
    [JsonPropertyName("local_lipsync")]
    public LocalLipSyncSettings LocalLipSync { get; set; } = new();
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

/// <summary>
/// A locally installed lip-sync tool. Command and arguments are a template so Wav2Lip,
/// SadTalker, video-retalking or anything else with a CLI works without a code change.
/// </summary>
public sealed class LocalLipSyncSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Executable to run, e.g. "python" or an absolute path to one.</summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Whitespace-separated arguments. Placeholders: {{VIDEO}} {{AUDIO}} {{IMAGE}} {{OUTPUT}}
    /// {{OUTPUT_DIR}} {{CHECKPOINT}}.
    /// </summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    [JsonPropertyName("checkpoint_path")]
    public string CheckpointPath { get; set; } = string.Empty;

    [JsonPropertyName("timeout_s")]
    public double TimeoutSeconds { get; set; } = 1800;
}

/// <summary>
/// How the presenter is separated from their original background and what replaces it.
/// Separation is deliberately explicit rather than automatic: guessing a matte on an arbitrary
/// photo produces halos and chewed hair, which looks worse than leaving the background alone.
/// </summary>
public sealed class BackgroundSettings
{
    /// <summary>none | chroma | matte | cutout</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "none";

    /// <summary>Key colour for a green or blue screen, as #RRGGBB.</summary>
    [JsonPropertyName("chroma_color")]
    public string ChromaColor { get; set; } = "#00B140";

    [JsonPropertyName("similarity")]
    public double Similarity { get; set; } = 0.18;

    [JsonPropertyName("blend")]
    public double Blend { get; set; } = 0.05;

    /// <summary>A greyscale matte the operator supplies: white keeps, black drops.</summary>
    [JsonPropertyName("matte_path")]
    public string MattePath { get; set; } = string.Empty;

    /// <summary>A tool that writes an RGBA cutout, e.g. rembg. Placeholders {{INPUT}} {{OUTPUT}}.</summary>
    [JsonPropertyName("cutout_command")]
    public string CutoutCommand { get; set; } = string.Empty;

    [JsonPropertyName("cutout_arguments")]
    public string CutoutArguments { get; set; } = string.Empty;

    /// <summary>blurred | solid | gradient | image</summary>
    [JsonPropertyName("backdrop")]
    public string Backdrop { get; set; } = "blurred";

    [JsonPropertyName("backdrop_color")]
    public string BackdropColor { get; set; } = "#101822";

    [JsonPropertyName("backdrop_image")]
    public string BackdropImage { get; set; } = string.Empty;

    public bool IsEnabled => !string.Equals(Mode, "none", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>Drive sway and breathing from the narration instead of a fixed oscillator.</summary>
    [JsonPropertyName("audio_reactive")]
    public bool AudioReactive { get; set; } = true;

    /// <summary>How much of the movement comes from speech rather than the idle drift, 0..1.</summary>
    [JsonPropertyName("audio_weight")]
    public double AudioWeight { get; set; } = 0.65;

    /// <summary>Distinct from <see cref="Background"/>, which only chooses how the frame is filled.</summary>
    [JsonPropertyName("background_replacement")]
    public BackgroundSettings BackgroundReplacement { get; set; } = new();
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


/// <summary>
/// Where a finished video can be uploaded. Deliberately provider-neutral in the same way the
/// talking-head route is: the operator supplies the endpoint, the auth header and a request
/// template, so a platform this app has never heard of works without a code change — and no
/// vendor is baked in as the assumed destination.
/// </summary>
public sealed class PublishSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>A name for the destination, used only in the record and the approval prompt.</summary>
    [JsonPropertyName("destination")]
    public string Destination { get; set; } = string.Empty;

    /// <summary>Endpoint that accepts the upload.</summary>
    [JsonPropertyName("upload_url")]
    public string UploadUrl { get; set; } = string.Empty;

    /// <summary>Header carrying the credential, e.g. Authorization.</summary>
    [JsonPropertyName("auth_header")]
    public string AuthHeader { get; set; } = "Authorization";

    [JsonPropertyName("auth_scheme")]
    public string AuthScheme { get; set; } = "Bearer";

    /// <summary>Name of the entry in secrets.json holding the token. Never the token itself.</summary>
    [JsonPropertyName("secret_key")]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// JSON metadata sent alongside the file. Placeholders: {{TITLE}}, {{DESCRIPTION}},
    /// {{TAGS}}, {{VISIBILITY}}.
    /// </summary>
    [JsonPropertyName("metadata_template")]
    public string MetadataTemplate { get; set; } = string.Empty;

    /// <summary>Form field the video file is sent under.</summary>
    [JsonPropertyName("file_field")]
    public string FileField { get; set; } = "file";

    /// <summary>Form field the JSON metadata is sent under.</summary>
    [JsonPropertyName("metadata_field")]
    public string MetadataField { get; set; } = "metadata";

    /// <summary>JSON path to the id in the reply, e.g. "id" or "data.video_id".</summary>
    [JsonPropertyName("id_path")]
    public string IdPath { get; set; } = "id";

    /// <summary>Template for the watch URL, with {{ID}} substituted.</summary>
    [JsonPropertyName("url_template")]
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>private | unlisted | public. Anything but private has to be chosen on purpose.</summary>
    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; set; } = "private";
}
