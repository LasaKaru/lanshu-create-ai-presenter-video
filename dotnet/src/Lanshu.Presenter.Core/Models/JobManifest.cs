using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lanshu.Presenter.Core.Models;

/// <summary>
/// Strongly typed port of assets/job.template.json. The wire format stays identical to the
/// Codex skill so job directories are interchangeable between the Python skill and this app.
/// </summary>
public sealed class JobManifest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = "replace-me";

    [JsonPropertyName("state")]
    public string State { get; set; } = "intake";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "automation";

    [JsonPropertyName("created_utc")]
    public string CreatedUtc { get; set; } = string.Empty;

    [JsonPropertyName("updated_utc")]
    public string UpdatedUtc { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public JobInput Input { get; set; } = new();

    [JsonPropertyName("creative")]
    public JobCreative Creative { get; set; } = new();

    [JsonPropertyName("voice")]
    public JobVoice Voice { get; set; } = new();

    [JsonPropertyName("plan")]
    public JobPlan Plan { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public JobCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("manual_input_review")]
    public ManualInputReview ManualInputReview { get; set; } = new();

    [JsonPropertyName("artifacts")]
    public JobArtifacts Artifacts { get; set; } = new();

    [JsonPropertyName("qa")]
    public JobQa Qa { get; set; } = new();

    [JsonPropertyName("history")]
    public List<JobHistoryEntry> History { get; set; } = new();

    [JsonIgnore]
    public JobState StateValue
    {
        get => JobStates.Parse(State);
        set => State = JobStates.ToWire(value);
    }

    public void Touch() => UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");

    public void Record(string stage, string message)
    {
        History.Add(new JobHistoryEntry
        {
            Utc = DateTimeOffset.UtcNow.ToString("O"),
            Stage = stage,
            Message = message,
        });

        // Keep the manifest readable; the full transcript lives in qa/reports/run-log.txt.
        if (History.Count > 400)
        {
            History.RemoveRange(0, History.Count - 400);
        }
    }
}

public sealed class JobInput
{
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("script_path")]
    public string ScriptPath { get; set; } = string.Empty;

    [JsonPropertyName("presenter_image")]
    public string PresenterImage { get; set; } = string.Empty;

    [JsonPropertyName("voice_sample")]
    public string VoiceSample { get; set; } = string.Empty;

    [JsonPropertyName("supporting_media")]
    public List<string> SupportingMedia { get; set; } = new();

    [JsonPropertyName("rights_confirmed")]
    public bool RightsConfirmed { get; set; }

    [JsonPropertyName("adult_presenter_confirmed")]
    public bool AdultPresenterConfirmed { get; set; }

    [JsonPropertyName("remote_upload_approved")]
    public bool RemoteUploadApproved { get; set; }

    [JsonPropertyName("voice_clone_approved")]
    public bool VoiceCloneApproved { get; set; }
}

public sealed class JobCreative
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    [JsonPropertyName("audience")]
    public string Audience { get; set; } = "general";

    [JsonPropertyName("goal")]
    public string Goal { get; set; } = "explain clearly";

    [JsonPropertyName("duration_target_s")]
    public double DurationTargetSeconds { get; set; } = 60;

    [JsonPropertyName("aspect")]
    public string Aspect { get; set; } = "9:16";

    [JsonPropertyName("width")]
    public int Width { get; set; } = 1080;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 1920;

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 30;

    [JsonPropertyName("style")]
    public string Style { get; set; } = "credible contemporary presenter";

    [JsonPropertyName("watermark")]
    public string Watermark { get; set; } = string.Empty;

    [JsonPropertyName("cta")]
    public string Cta { get; set; } = string.Empty;

    [JsonPropertyName("captions_enabled")]
    public bool CaptionsEnabled { get; set; } = true;

    [JsonPropertyName("keyword_callouts_enabled")]
    public bool KeywordCalloutsEnabled { get; set; } = true;

    /// <summary>Push the frame in slightly on each keyword beat.</summary>
    [JsonPropertyName("punch_ins_enabled")]
    public bool PunchInsEnabled { get; set; } = true;

    /// <summary>Extra aspect ratios to deliver from the same locked narration.</summary>
    [JsonPropertyName("additional_aspects")]
    public List<string> AdditionalAspects { get; set; } = new();

    [JsonPropertyName("publishing_kit")]
    public bool PublishingKit { get; set; } = true;

    [JsonPropertyName("caption_font")]
    public string CaptionFont { get; set; } = string.Empty;

    [JsonPropertyName("accent_color")]
    public string AccentColor { get; set; } = "#F4C430";

    [JsonPropertyName("music_path")]
    public string MusicPath { get; set; } = string.Empty;

    [JsonPropertyName("music_gain_db")]
    public double MusicGainDb { get; set; } = -22;
}

public sealed class JobVoice
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = "auto";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("voice_id")]
    public string VoiceId { get; set; } = string.Empty;

    [JsonPropertyName("rate")]
    public double Rate { get; set; } = 1.06;

    [JsonPropertyName("segment_lufs")]
    public double SegmentLufs { get; set; } = -17;

    [JsonPropertyName("program_lufs")]
    public double ProgramLufs { get; set; } = -16;

    /// <summary>Voice id created from the authorized sample, if one was cloned for this job.</summary>
    [JsonPropertyName("cloned_voice_id")]
    public string ClonedVoiceId { get; set; } = string.Empty;

    [JsonPropertyName("sections")]
    public List<VoiceSection> Sections { get; set; } = new();
}

public sealed class VoiceSection
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>The script wording. Captions and keyword anchors always use this.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// What is actually sent to the speech engine, when it differs from the script — a
    /// respelling that fixes a mispronunciation, for instance. Captions keep the script wording.
    /// </summary>
    [JsonPropertyName("spoken_override")]
    public string SpokenOverride { get; set; } = string.Empty;

    /// <summary>Set to force a re-synthesis of this segment on the next run.</summary>
    [JsonPropertyName("retake_requested")]
    public bool RetakeRequested { get; set; }

    [JsonIgnore]
    public string EffectiveSpokenText =>
        string.IsNullOrWhiteSpace(SpokenOverride) ? Text : SpokenOverride;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("duration_s")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("start_s")]
    public double StartSeconds { get; set; }
}

public sealed class JobPlan
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "draft";

    [JsonPropertyName("route")]
    public string Route { get; set; } = "presenter_led";

    [JsonPropertyName("opening_target_s")]
    public double OpeningTargetSeconds { get; set; } = 4;

    [JsonPropertyName("closing_target_s")]
    public double ClosingTargetSeconds { get; set; } = 5;

    [JsonPropertyName("chapters")]
    public List<PlanChapter> Chapters { get; set; } = new();

    [JsonPropertyName("requested_generation_seconds")]
    public double RequestedGenerationSeconds { get; set; }

    [JsonPropertyName("estimated_cost")]
    public double? EstimatedCost { get; set; }

    [JsonPropertyName("price_evidence_date")]
    public string PriceEvidenceDate { get; set; } = string.Empty;

    /// <summary>Pause after drafting so the narration can be read and edited before it is spoken.</summary>
    [JsonPropertyName("review_script")]
    public bool ReviewScript { get; set; }

    [JsonPropertyName("script_approved")]
    public bool ScriptApproved { get; set; }

    [JsonPropertyName("pilot_approved")]
    public bool PilotApproved { get; set; }

    [JsonPropertyName("paid_generation_approved")]
    public bool PaidGenerationApproved { get; set; }

    [JsonPropertyName("retry_ceiling")]
    public int RetryCeiling { get; set; } = 3;

    [JsonPropertyName("rejected_candidates")]
    public int RejectedCandidates { get; set; }
}

public sealed class PlanChapter
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "beat";

    [JsonPropertyName("start_s")]
    public double StartSeconds { get; set; }

    [JsonPropertyName("duration_s")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("supporting_media")]
    public string SupportingMedia { get; set; } = string.Empty;
}

public sealed class JobCapabilities
{
    [JsonPropertyName("voice_generation")]
    public CapabilityRecord VoiceGeneration { get; set; } = new();

    [JsonPropertyName("main_presenter")]
    public CapabilityRecord MainPresenter { get; set; } = new();

    [JsonPropertyName("short_motion")]
    public CapabilityRecord ShortMotion { get; set; } = new();

    [JsonPropertyName("lipsync_repair")]
    public CapabilityRecord LipsyncRepair { get; set; } = new();

    [JsonPropertyName("word_timestamp_asr")]
    public CapabilityRecord WordTimestampAsr { get; set; } = new();

    [JsonPropertyName("timeline_compositor")]
    public CapabilityRecord TimelineCompositor { get; set; } = new();

    [JsonPropertyName("encoder_qa")]
    public CapabilityRecord EncoderQa { get; set; } = new();
}

/// <summary>
/// Records the tool actually used for a capability, as required by the skill's
/// "record the actual choices per job" rule. Never holds credentials.
/// </summary>
public sealed class CapabilityRecord
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public JsonObject Parameters { get; set; } = new();

    [JsonPropertyName("task_ids")]
    public List<string> TaskIds { get; set; } = new();

    [JsonPropertyName("price_evidence_date")]
    public string PriceEvidenceDate { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsRecorded => !string.IsNullOrWhiteSpace(Provider);
}

public sealed class ManualInputReview
{
    [JsonPropertyName("image_viewed")]
    public bool ImageViewed { get; set; }

    [JsonPropertyName("single_clear_face")]
    public bool SingleClearFace { get; set; }

    [JsonPropertyName("image_has_no_unwanted_text")]
    public bool ImageHasNoUnwantedText { get; set; }

    [JsonPropertyName("voice_sample_listened")]
    public bool VoiceSampleListened { get; set; }

    [JsonPropertyName("single_clear_speaker")]
    public bool SingleClearSpeaker { get; set; }
}

public sealed class JobArtifacts
{
    [JsonPropertyName("script")]
    public string Script { get; set; } = string.Empty;

    [JsonPropertyName("beat_sheet")]
    public string BeatSheet { get; set; } = string.Empty;

    [JsonPropertyName("timeline")]
    public string Timeline { get; set; } = string.Empty;

    [JsonPropertyName("storyboard")]
    public string Storyboard { get; set; } = string.Empty;

    [JsonPropertyName("final_audio")]
    public string FinalAudio { get; set; } = string.Empty;

    [JsonPropertyName("caption_json")]
    public string CaptionJson { get; set; } = string.Empty;

    [JsonPropertyName("caption_srt")]
    public string CaptionSrt { get; set; } = string.Empty;

    [JsonPropertyName("caption_ass")]
    public string CaptionAss { get; set; } = string.Empty;

    [JsonPropertyName("presenter_plate")]
    public string PresenterPlate { get; set; } = string.Empty;

    [JsonPropertyName("rendered")]
    public string Rendered { get; set; } = string.Empty;

    [JsonPropertyName("master")]
    public string Master { get; set; } = string.Empty;

    [JsonPropertyName("share")]
    public string Share { get; set; } = string.Empty;

    [JsonPropertyName("contact_sheet")]
    public string ContactSheet { get; set; } = string.Empty;

    [JsonPropertyName("alternate_masters")]
    public List<string> AlternateMasters { get; set; } = new();

    [JsonPropertyName("thumbnails")]
    public List<string> Thumbnails { get; set; } = new();

    [JsonPropertyName("chapters")]
    public string Chapters { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

public sealed class JobQa
{
    [JsonPropertyName("preflight_report")]
    public string PreflightReport { get; set; } = string.Empty;

    [JsonPropertyName("asr_report")]
    public string AsrReport { get; set; } = string.Empty;

    [JsonPropertyName("composition_report")]
    public string CompositionReport { get; set; } = string.Empty;

    [JsonPropertyName("delivery_report")]
    public string DeliveryReport { get; set; } = string.Empty;

    [JsonPropertyName("manual_visual_review")]
    public string ManualVisualReview { get; set; } = string.Empty;
}

public sealed class JobHistoryEntry
{
    [JsonPropertyName("utc")]
    public string Utc { get; set; } = string.Empty;

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public static class JobJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException($"could not deserialize {typeof(T).Name}");
}
