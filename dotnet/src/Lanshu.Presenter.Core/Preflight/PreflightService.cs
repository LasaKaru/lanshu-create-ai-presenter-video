using System.Globalization;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Preflight;

/// <summary>
/// Port of scripts/preflight.py. Errors block the whole job; remote blockers only block
/// uploads and paid generation, so a fully local render can still proceed.
/// </summary>
public sealed class PreflightService
{
    private readonly FfmpegService _ffmpeg;

    public PreflightService(FfmpegService ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    public async Task<PreflightReport> RunAsync(
        JobPaths paths,
        JobManifest job,
        CancellationToken cancellationToken = default)
    {
        var report = new PreflightReport { Job = Path.GetFileName(paths.ManifestFile) };
        var input = job.Input;

        var topic = input.Topic.Trim();
        var scriptPath = input.ScriptPath.Trim();
        if (string.IsNullOrEmpty(topic) && string.IsNullOrEmpty(scriptPath))
        {
            report.Errors.Add("topic or script_path is required");
        }

        if (!string.IsNullOrEmpty(scriptPath))
        {
            RequireFile(scriptPath, "script", report.Errors);
        }

        var image = RequireFile(input.PresenterImage, "presenter image", report.Errors);
        if (image is not null)
        {
            try
            {
                var probe = await _ffmpeg.ProbeAsync(image, cancellationToken).ConfigureAwait(false);
                report.Media["presenter_image"] = probe.PortableRaw();
                var video = probe.Video;
                if (video is null)
                {
                    report.Errors.Add("presenter image has no decodable image/video stream");
                }
                else
                {
                    if (Math.Min(video.Width, video.Height) < 512)
                    {
                        report.Warnings.Add(
                            $"presenter image is low resolution: {video.Width}x{video.Height}");
                    }

                    if (Math.Min(video.Width, video.Height) < 256)
                    {
                        report.Errors.Add(
                            $"presenter image is too small to render from: {video.Width}x{video.Height}");
                    }
                }
            }
            catch (Exception exception)
            {
                report.Errors.Add($"could not decode presenter image: {exception.Message}");
            }
        }

        var voiceValue = input.VoiceSample.Trim();
        if (!string.IsNullOrEmpty(voiceValue))
        {
            var voice = RequireFile(voiceValue, "voice sample", report.Errors);
            if (voice is not null)
            {
                try
                {
                    var probe = await _ffmpeg.ProbeAsync(voice, cancellationToken).ConfigureAwait(false);
                    report.Media["voice_sample"] = probe.PortableRaw();
                    if (!probe.HasAudio)
                    {
                        report.Errors.Add("voice sample has no audio stream");
                    }

                    var duration = probe.DurationSeconds;
                    if (duration < 4)
                    {
                        report.Warnings.Add(
                            $"voice sample is short: {duration.ToString("0.000", CultureInfo.InvariantCulture)}s");
                    }

                    if (duration > 60)
                    {
                        report.Warnings.Add(
                            $"voice sample is unusually long: {duration.ToString("0.000", CultureInfo.InvariantCulture)}s");
                    }
                }
                catch (Exception exception)
                {
                    report.Errors.Add($"could not decode voice sample: {exception.Message}");
                }
            }

            if (!input.VoiceCloneApproved)
            {
                report.RemoteBlockers.Add("voice_clone_approved must be true before voice cloning");
            }
        }
        else
        {
            report.Warnings.Add("no voice sample supplied; a stock or system voice will be used and recorded");
        }

        var supporting = new JsonArray();
        foreach (var value in input.SupportingMedia)
        {
            var path = RequireFile(value, "supporting media", report.Errors);
            if (path is null)
            {
                continue;
            }

            try
            {
                var probe = await _ffmpeg.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
                supporting.Add(new JsonObject
                {
                    ["file"] = Path.GetFileName(path),
                    ["probe"] = probe.PortableRaw(),
                });
            }
            catch (Exception exception)
            {
                report.Errors.Add($"could not decode supporting media {Path.GetFileName(path)}: {exception.Message}");
            }
        }

        report.Media["supporting_media"] = supporting;

        if (!string.IsNullOrWhiteSpace(job.Creative.MusicPath))
        {
            var music = RequireFile(job.Creative.MusicPath, "music bed", report.Errors);
            if (music is not null)
            {
                try
                {
                    var probe = await _ffmpeg.ProbeAsync(music, cancellationToken).ConfigureAwait(false);
                    report.Media["music"] = probe.PortableRaw();
                    if (!probe.HasAudio)
                    {
                        report.Errors.Add("music bed has no audio stream");
                    }
                }
                catch (Exception exception)
                {
                    report.Errors.Add($"could not decode music bed: {exception.Message}");
                }
            }
        }

        if (!input.RightsConfirmed)
        {
            report.RemoteBlockers.Add("rights_confirmed must be true before presenter synthesis");
        }

        if (!input.AdultPresenterConfirmed)
        {
            report.RemoteBlockers.Add("adult_presenter_confirmed must be true before presenter synthesis");
        }

        if (!input.RemoteUploadApproved)
        {
            report.RemoteBlockers.Add("remote_upload_approved must be true before remote generation");
        }

        var manual = job.ManualInputReview;
        if (!manual.ImageViewed)
        {
            report.Errors.Add("manual_input_review.image_viewed must be true");
        }

        if (!manual.SingleClearFace)
        {
            report.Errors.Add("manual_input_review.single_clear_face must be true");
        }

        if (!manual.ImageHasNoUnwantedText)
        {
            report.Errors.Add("manual_input_review.image_has_no_unwanted_text must be true");
        }

        if (!string.IsNullOrEmpty(voiceValue))
        {
            if (!manual.VoiceSampleListened)
            {
                report.Errors.Add("manual_input_review.voice_sample_listened must be true");
            }

            if (!manual.SingleClearSpeaker)
            {
                report.Errors.Add("manual_input_review.single_clear_speaker must be true");
            }
        }

        var creative = job.Creative;
        if (creative.DurationTargetSeconds is < 5 or > 1800)
        {
            report.Errors.Add("creative.duration_target_s must be between 5 and 1800");
        }

        if (Math.Min(creative.Width, creative.Height) < 256 || Math.Max(creative.Width, creative.Height) > 7680)
        {
            report.Errors.Add("creative width/height must be between 256 and 7680");
        }

        if (creative.Width % 2 != 0 || creative.Height % 2 != 0)
        {
            report.Errors.Add("creative width/height must both be even for H.264 output");
        }

        if (!JobService.AllowedFps.Contains(creative.Fps))
        {
            report.Errors.Add("creative.fps must be one of 24, 25, 30, 50, or 60");
        }

        foreach (var missing in _ffmpeg.Toolset.MissingRequirements())
        {
            report.Errors.Add($"the configured FFmpeg build is missing the {missing}");
        }

        if (creative.CaptionsEnabled && !_ffmpeg.Toolset.HasSubtitleBurnIn)
        {
            report.Warnings.Add(
                "this FFmpeg build cannot burn in subtitles; captions will be delivered as a sidecar .srt only");
        }

        report.Ok = report.Errors.Count == 0;
        report.RemoteReady = report.Ok && report.RemoteBlockers.Count == 0;

        var reportPath = Path.Combine(paths.QaReports, "preflight.json");
        FileSystemUtil.WriteAtomic(reportPath, JobJson.Serialize(report));
        job.Qa.PreflightReport = paths.Relative(reportPath);
        return report;
    }

    private static string? RequireFile(string value, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"missing {label}");
            return null;
        }

        try
        {
            var path = FileSystemUtil.ExpandPath(value);
            if (!File.Exists(path))
            {
                errors.Add($"{label} is not a file: {path}");
                return null;
            }

            return path;
        }
        catch (Exception exception)
        {
            errors.Add($"{label} path is not usable: {exception.Message}");
            return null;
        }
    }
}
