using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Asr;
using Lanshu.Presenter.Core.Delivery;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Presenter;
using Lanshu.Presenter.Core.Timeline;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Qa;

public sealed class GateResult
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("blocking")]
    public bool Blocking { get; set; } = true;
}

public sealed class QaReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("checked_utc")]
    public string CheckedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("gates")]
    public List<GateResult> Gates { get; set; } = new();

    [JsonPropertyName("manual_review_required")]
    public List<string> ManualReviewRequired { get; set; } = new();
}

/// <summary>
/// The machine half of the acceptance gates in qa-recovery.md. It proves the numbers; the
/// contact sheet and full playback still carry the visual half, and this report says so.
/// </summary>
public sealed class QaService
{
    private readonly FfmpegService _ffmpeg;

    public QaService(FfmpegService ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    public async Task<QaReport> RunAsync(
        JobPaths paths,
        JobManifest job,
        RenderTimeline timeline,
        DeliveryReport delivery,
        AsrVerificationReport asr,
        PresenterPlate plate,
        CancellationToken cancellationToken = default,
        double cardSeconds = 0)
    {
        var report = new QaReport();
        var creative = job.Creative;

        var masterPath = Path.Combine(paths.Outputs, delivery.Master);
        var sharePath = Path.Combine(paths.Outputs, delivery.Share);

        report.Gates.Add(new GateResult
        {
            Name = "master_exists",
            Passed = File.Exists(masterPath) && FileSystemUtil.SafeLength(masterPath) > 0,
            Detail = delivery.Master,
        });

        report.Gates.Add(new GateResult
        {
            Name = "share_exists",
            Passed = File.Exists(sharePath) && FileSystemUtil.SafeLength(sharePath) > 0,
            Detail = delivery.Share,
        });

        report.Gates.Add(new GateResult
        {
            Name = "full_decode",
            Passed = delivery.FullDecodePassed,
            Detail = "master and share fully decoded",
        });

        if (File.Exists(masterPath))
        {
            var probe = await _ffmpeg.ProbeAsync(masterPath, cancellationToken).ConfigureAwait(false);
            var video = probe.Video;
            var audio = probe.Audio;

            report.Gates.Add(new GateResult
            {
                Name = "dimensions",
                Passed = video is not null && video.Width == creative.Width && video.Height == creative.Height,
                Detail = video is null
                    ? "no video stream"
                    : $"{video.Width}x{video.Height} (expected {creative.Width}x{creative.Height})",
            });

            var actualFps = video?.FrameRate ?? 0;
            report.Gates.Add(new GateResult
            {
                Name = "frame_rate",
                Passed = Math.Abs(actualFps - creative.Fps) < 0.5,
                Detail = $"{actualFps:0.###} fps (expected {creative.Fps})",
            });

            report.Gates.Add(new GateResult
            {
                Name = "audio_format",
                Passed = audio is not null
                         && audio.Channels == 2
                         && string.Equals(audio.SampleRate, "48000", StringComparison.Ordinal),
                Detail = audio is null
                    ? "no audio stream"
                    : $"{audio.CodecName} {audio.SampleRate}Hz {audio.Channels}ch",
            });

            // Title and end cards are joined onto the program after the edit, so the delivered
            // file is legitimately longer than the narration. The gate still measures the edit:
            // it adds the card length it was told about rather than widening its tolerance.
            var expected = timeline.DurationSeconds + cardSeconds;
            var drift = Math.Abs(probe.DurationSeconds - expected);
            var against = cardSeconds > 0.001
                ? $"a {timeline.DurationSeconds:0.000}s narration timeline plus {cardSeconds:0.000}s of cards"
                : $"a {timeline.DurationSeconds:0.000}s narration timeline";
            report.Gates.Add(new GateResult
            {
                Name = "duration_matches_narration",
                Passed = drift <= 0.5,
                Detail = $"{probe.DurationSeconds:0.000}s against {against} (drift {drift:0.000}s)",
            });

            // The delivered program must land on target loudness; measure the file, not the sections.
            var loudness = await _ffmpeg
                .MeasureLoudnessAsync(masterPath, job.Voice.ProgramLufs, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var deviation = Math.Abs(loudness.InputI - job.Voice.ProgramLufs);
            report.Gates.Add(new GateResult
            {
                Name = "program_loudness",
                Passed = deviation <= 1.0,
                Detail = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.0} LUFS against a {1:0.0} LUFS target (deviation {2:0.0} LU)",
                    loudness.InputI,
                    job.Voice.ProgramLufs,
                    deviation),
            });

            report.Gates.Add(new GateResult
            {
                Name = "true_peak",
                Passed = loudness.InputTp <= -0.5,
                Detail = $"{loudness.InputTp:0.0} dBTP",
            });
        }

        report.Gates.Add(new GateResult
        {
            Name = "no_black_frame_runs",
            Passed = delivery.BlackFrameEvents == 0,
            Detail = $"{delivery.BlackFrameEvents} black-frame events longer than 100ms",
            Blocking = false,
        });

        report.Gates.Add(new GateResult
        {
            Name = "contact_sheet",
            Passed = File.Exists(Path.Combine(paths.Outputs, delivery.ContactSheet)),
            Detail = delivery.ContactSheet,
        });

        if (asr.Checked)
        {
            report.Gates.Add(new GateResult
            {
                Name = "narration_matches_script",
                Passed = asr.MatchRatio >= 0.90,
                Detail = $"transcription matched {asr.MatchRatio:P0} of the script",
                Blocking = false,
            });
        }

        report.Gates.Add(new GateResult
        {
            Name = "presenter_source_recorded",
            Passed = !string.IsNullOrWhiteSpace(plate.Provider),
            Detail = $"{plate.Provider}{(string.IsNullOrWhiteSpace(plate.Model) ? string.Empty : " / " + plate.Model)}",
        });

        report.ManualReviewRequired.Add(
            "Watch the complete video at normal speed before publishing: identity, mouth timing, blinking, gestures, hands, lighting and continuity.");
        report.ManualReviewRequired.Add(
            $"Inspect the nine-frame contact sheet ({delivery.ContactSheet}) for the opening, chapter cuts, keyword beats, captions and the final frame.");

        if (!plate.HasSynchronizedMouth)
        {
            report.ManualReviewRequired.Add(
                "The presenter track is an animated motion plate, not a lip-synced talking head. Do not describe it as a speaking presenter unless a lip-sync provider was used.");
        }

        if (!asr.Checked)
        {
            report.ManualReviewRequired.Add(
                "No transcription service ran, so the narration was not machine-verified against the script. Listen to it once end to end.");
        }

        report.Ok = report.Gates.Where(gate => gate.Blocking).All(gate => gate.Passed);

        var reportPath = Path.Combine(paths.QaReports, "acceptance.json");
        FileSystemUtil.WriteAtomic(reportPath, JobJson.Serialize(report));
        job.Qa.CompositionReport = paths.Relative(reportPath);
        return report;
    }

    public static string ToMarkdown(QaReport report, DeliveryReport delivery)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Delivery QA");
        builder.AppendLine();
        builder.AppendLine($"- machine gates: **{(report.Ok ? "passed" : "FAILED")}**");
        builder.AppendLine($"- duration: `{delivery.DurationSeconds:0.000}s`");
        builder.AppendLine($"- master: `{delivery.Master}` ({delivery.MasterBytes / 1024 / 1024.0:0.0} MB)");
        builder.AppendLine($"- share: `{delivery.Share}` ({delivery.ShareBytes / 1024 / 1024.0:0.0} MB)");
        builder.AppendLine();
        builder.AppendLine("| Gate | Result | Detail |");
        builder.AppendLine("|------|--------|--------|");

        foreach (var gate in report.Gates)
        {
            var mark = gate.Passed ? "pass" : gate.Blocking ? "**FAIL**" : "warn";
            builder.AppendLine($"| {gate.Name} | {mark} | {gate.Detail} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Visual review still required");
        builder.AppendLine();
        foreach (var item in report.ManualReviewRequired)
        {
            builder.AppendLine($"- [ ] {item}");
        }

        builder.AppendLine();
        return builder.ToString();
    }
}
