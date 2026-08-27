using System.Text;
using Lanshu.Presenter.Core.Asr;
using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Delivery;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Localization;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Preflight;
using Lanshu.Presenter.Core.Presenter;
using Lanshu.Presenter.Core.Qa;
using Lanshu.Presenter.Core.Timeline;
using Lanshu.Presenter.Core.Util;
using Lanshu.Presenter.Core.Voice;

namespace Lanshu.Presenter.Core.Pipeline;

/// <summary>
/// Drives one job through the production state machine from SKILL.md. A stage runs only when its
/// inputs exist, and the job advances only when its evidence is on disk, so an interrupted run
/// resumes from the earliest unfinished state without regenerating accepted work.
/// </summary>
public sealed class PresenterVideoPipeline
{
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;
    private readonly JobService _jobs = new();

    public PresenterVideoPipeline(SettingsStore store, HttpClient httpClient)
    {
        _store = store;
        _httpClient = httpClient;
    }

    public async Task<PipelineResult> RunAsync(
        JobPaths paths,
        PipelineOptions options,
        IProgress<PipelineEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _store.Load();
        var job = _jobs.Load(paths);
        var logFile = paths.RunLog;
        Directory.CreateDirectory(paths.QaReports);
        Directory.CreateDirectory(paths.Temp);

        var logLock = new object();
        void Log(string message)
        {
            var line = $"{DateTimeOffset.UtcNow:O}  {message}";
            lock (logLock)
            {
                try
                {
                    File.AppendAllText(logFile, line + System.Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                    // A locked log file must never take the render down.
                }
            }
        }

        void Report(string stage, string message, double fraction)
        {
            Log($"[{stage}] {message}");
            progress?.Report(new PipelineEvent(stage, message, fraction));
        }

        try
        {
            Report("environment", "Locating FFmpeg", 0.01);
            var toolset = await MediaToolset
                .ResolveAsync(settings.FfmpegPath, settings.FfprobePath, cancellationToken)
                .ConfigureAwait(false);
            var ffmpeg = new FfmpegService(toolset, Log);
            ffmpeg.Encoder = await new VideoEncoderSelector(toolset, Log)
                .ResolveAsync(settings.Render.Encoder, cancellationToken)
                .ConfigureAwait(false);
            Report(
                "environment",
                $"Using {Path.GetFileName(toolset.FfmpegPath)} — {toolset.Version}; video encoder: {ffmpeg.Encoder.DisplayName}",
                0.02);

            job.Capabilities.EncoderQa.Provider = "ffmpeg";
            job.Capabilities.EncoderQa.Version = toolset.Version;
            job.Capabilities.EncoderQa.Model = ffmpeg.Encoder.Name;
            job.Capabilities.TimelineCompositor.Provider = "ffmpeg";
            job.Capabilities.TimelineCompositor.Model = "filter_complex";

            // 1. Preflight -------------------------------------------------------------
            Report("preflight", "Checking inputs and approvals", 0.04);
            var preflight = await new PreflightService(ffmpeg)
                .RunAsync(paths, job, cancellationToken)
                .ConfigureAwait(false);
            _jobs.Save(paths, job);

            if (!preflight.Ok)
            {
                var detail = string.Join("; ", preflight.Errors);
                Report("preflight", "Preflight failed: " + detail, 0.04);
                return new PipelineResult
                {
                    Outcome = PipelineOutcome.Failed,
                    State = job.StateValue,
                    Message = "Preflight failed: " + detail,
                    Warnings = preflight.Warnings.ToList(),
                };
            }

            var warnings = preflight.Warnings.ToList();

            // 2. Content ---------------------------------------------------------------
            Report("content_locked", "Preparing the script", 0.08);
            var script = await LoadOrWriteScriptAsync(paths, job, settings, options, Report, cancellationToken)
                .ConfigureAwait(false);
            _jobs.Advance(paths, job, JobState.ContentLocked, $"script locked: {script.Beats.Count} beats, {script.Source}");

            // Narration is expensive and everything downstream is timed against it, so the
            // wording is settled before a single word is spoken.
            if ((job.Plan.ReviewScript || settings.Script.ReviewBeforeAudio) && !job.Plan.ScriptApproved)
            {
                Report("content_locked", "Waiting for script review", 0.12);
                return new PipelineResult
                {
                    Outcome = PipelineOutcome.NeedsApproval,
                    State = job.StateValue,
                    ApprovalKind = "script",
                    ApprovalRequest =
                        $"Read the {script.Beats.Count}-beat narration and edit anything that should sound different, "
                        + "then approve it. Nothing is synthesized until you do.",
                    Message = "The script is ready for review.",
                    Warnings = warnings,
                };
            }

            // 3. Audio -----------------------------------------------------------------
            Report("audio_locked", "Choosing a voice", 0.14);
            var router = new SpeechRouter(settings, _store, _httpClient);
            var synthesizer = await router.ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(job.Voice.VoiceId) && !string.IsNullOrWhiteSpace(settings.Voice.VoiceId))
            {
                job.Voice.VoiceId = settings.Voice.VoiceId;
            }

            if (synthesizer.IsRemote && !job.Input.RemoteUploadApproved)
            {
                warnings.Add(
                    $"the '{synthesizer.Provider}' voice sends narration text to a remote service; remote_upload_approved is not set on this job");
            }

            // A voice is a person's likeness. Cloning only happens when the sample owner's
            // permission and the upload approval are both recorded, and the engine supports it.
            if (string.IsNullOrWhiteSpace(job.Voice.VoiceId) && !string.IsNullOrWhiteSpace(job.Input.VoiceSample))
            {
                var eligibility = CloneEligibility.Evaluate(
                    job.Input.VoiceSample,
                    job.Input.VoiceCloneApproved,
                    job.Input.RemoteUploadApproved,
                    alreadyHaveVoiceId: false);

                if (synthesizer is not IVoiceCloner cloner)
                {
                    warnings.Add(
                        $"a voice sample was supplied but the '{synthesizer.Provider}' engine cannot clone voices; a stock voice is used instead");
                }
                else if (!eligibility.Allowed)
                {
                    warnings.Add($"the voice sample was not cloned: {eligibility.Reason}");
                }
                else
                {
                    Report("audio_locked", "Cloning the authorized voice sample", 0.16);
                    try
                    {
                        var cloned = await cloner
                            .CloneAsync($"lanshu-{job.JobId}", job.Input.VoiceSample, cancellationToken)
                            .ConfigureAwait(false);

                        job.Voice.VoiceId = cloned.VoiceId;
                        job.Voice.ClonedVoiceId = cloned.VoiceId;
                        job.Capabilities.VoiceGeneration.Notes =
                            $"voice cloned from the authorized sample as '{cloned.Name}'";
                        job.Record("audio_locked", $"voice cloned via {cloned.Provider}");
                        _jobs.Save(paths, job);

                        warnings.Add(
                            $"a voice named '{cloned.Name}' was created in your {cloned.Provider} account (id {cloned.VoiceId}). Delete it there when the job is finished.");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        warnings.Add($"voice cloning failed, falling back to a stock voice: {exception.Message}");
                    }
                }
            }

            Report("audio_locked", $"Synthesizing narration with {synthesizer.Provider}", 0.18);
            var narration = await new NarrationService(ffmpeg, Log)
                .BuildAsync(paths, job, script, synthesizer, cancellationToken)
                .ConfigureAwait(false);
            _jobs.Advance(paths, job, JobState.AudioLocked,
                $"narration locked: {narration.DurationSeconds:0.000}s via {narration.Provider}");
            Report("audio_locked", $"Narration locked at {narration.DurationSeconds:0.00}s", 0.34);

            if (options.AudioOnly)
            {
                return new PipelineResult
                {
                    Outcome = PipelineOutcome.Completed,
                    State = job.StateValue,
                    Message = $"Narration locked at {narration.DurationSeconds:0.00}s",
                    DurationSeconds = narration.DurationSeconds,
                    Warnings = warnings,
                };
            }

            // 4. Verification and captions ---------------------------------------------
            Report("visual_plan_locked", "Deriving word timings", 0.38);
            var (asrReport, captionPlan) = await BuildCaptionsAsync(
                    paths, job, settings, script, narration, ffmpeg, Report, cancellationToken)
                .ConfigureAwait(false);
            warnings.AddRange(asrReport.Warnings);

            var subtitlePath = job.Creative.CaptionsEnabled || job.Creative.KeywordCalloutsEnabled
                ? paths.Resolve(job.Artifacts.CaptionAss)
                : string.Empty;

            // 5. Presenter route and billing gate ---------------------------------------
            var presenterRouter = new PresenterRouter(settings, _store, _httpClient, ffmpeg, Log);
            var route = presenterRouter.Resolve(job, paths);
            Report("visual_plan_locked", $"Presenter route: {route.Generator.Provider} — {route.Reason}", 0.42);

            job.Plan.Route = route.Generator.IsRemote ? "remote_presenter" : "local_motion_plate";
            job.Plan.RequestedGenerationSeconds = Math.Round(narration.DurationSeconds, 2);
            job.Plan.Status = "locked";
            _jobs.Advance(paths, job, JobState.VisualPlanLocked, $"visual plan locked via {route.Generator.Provider}");

            var plateDirectory = paths.VideoSelected;
            Directory.CreateDirectory(plateDirectory);
            Directory.CreateDirectory(paths.VideoCandidates);
            var platePath = options.Preview
                ? Path.Combine(paths.VideoCandidates, "preview-plate.mp4")
                : Path.Combine(plateDirectory, "presenter.mp4");

            // The proxy keeps the delivery aspect exactly; only the pixel count drops.
            var renderWidth = job.Creative.Width;
            var renderHeight = job.Creative.Height;
            if (options.Preview)
            {
                (renderWidth, renderHeight) = ProxyDimensions(
                    job.Creative.Width,
                    job.Creative.Height,
                    options.PreviewHeight <= 0 ? settings.Render.PreviewHeight : options.PreviewHeight);
                Report("presenter_generated", $"Preview mode: rendering at {renderWidth}x{renderHeight}", 0.46);
            }

            // Framing and emphasis crop into the plate, so it is rendered large enough that those
            // moves take a window out of real detail instead of upscaling a finished frame.
            // Chapters come from the script and the measured narration, so they can be settled
            // here rather than waiting for the timeline — the plate has to be sized before then.
            job.Plan.Chapters = TimelineBuilder.BuildChapters(script, narration);

            var plannedShots = job.Creative.MultiShotEnabled
                ? TimelineBuilder.BuildShots(job.Plan.Chapters, narration.DurationSeconds)
                : Array.Empty<Shot>();

            var tightestFraming = plannedShots.Count > 0 ? plannedShots.Max(shot => shot.Scale) : 1.0;
            var oversample = Math.Clamp(tightestFraming * (job.Creative.PunchInsEnabled ? 1.05 : 1.0), 1.0, 1.6);

            var envelope = settings.Presenter.Motion.AudioReactive && !options.Preview
                ? await AudioEnvelope.MeasureAsync(
                        ffmpeg,
                        narration.AudioPath,
                        job.Creative.Fps,
                        (int)Math.Round(narration.DurationSeconds * job.Creative.Fps),
                        cancellationToken)
                    .ConfigureAwait(false)
                : null;

            // The presenter can be lifted off their original background before anything animates
            // them; the plate then treats the composite as the source image like any other.
            var sourceImage = job.Input.PresenterImage;
            if (settings.Presenter.Motion.BackgroundReplacement.IsEnabled && !route.Generator.IsRemote)
            {
                Report("presenter_generated", "Replacing the background", 0.47);
                sourceImage = await new BackgroundCompositor(
                        ffmpeg,
                        settings.Presenter.Motion.BackgroundReplacement,
                        Log)
                    .PrepareAsync(
                        job.Input.PresenterImage,
                        Path.Combine(paths.Temp, "background"),
                        job.Creative.Width,
                        job.Creative.Height,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!string.Equals(sourceImage, job.Input.PresenterImage, StringComparison.Ordinal))
                {
                    job.Capabilities.ShortMotion.Notes =
                        $"background replaced via '{settings.Presenter.Motion.BackgroundReplacement.Mode}'";
                }
            }

            PresenterPlate plate;

            if (route.Generator.IsRemote)
            {
                var gate = await RunRemotePresenterAsync(
                        paths, job, settings, route, narration, platePath, Report, cancellationToken)
                    .ConfigureAwait(false);

                if (gate.Blocked is not null)
                {
                    _jobs.Save(paths, job);
                    return gate.Blocked;
                }

                plate = gate.Plate!;
            }
            else
            {
                Report("presenter_generated", "Rendering the local motion plate", 0.48);
                plate = await route.Generator.GenerateAsync(
                        new PresenterRequest
                        {
                            ImagePath = sourceImage,
                            AudioPath = narration.AudioPath,
                            OutputPath = platePath,
                            DurationSeconds = narration.DurationSeconds,
                            Width = renderWidth,
                            Height = renderHeight,
                            Fps = job.Creative.Fps,
                            Prompt = PresenterPromptBuilder.Build(job, actionfulOpening: true),
                            NegativePrompt = PresenterPromptBuilder.NegativePrompt(),
                            IsPilot = options.Preview,
                            Oversample = options.Preview ? 1.0 : oversample,
                            Envelope = envelope,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            }

            // Which generator produced the motion is a separate fact from which tool synced the
            // mouth, so the plate's own provider is captured before any repair replaces it.
            var generatorPlate = plate;

            // A motion plate has no mouth sync of its own; a lip-sync tool can supply it while
            // preserving the accepted motion, which is exactly the repair qa-recovery.md describes.
            if (!plate.HasSynchronizedMouth && !options.Preview)
            {
                var repaired = await presenterRouter
                    .ApplyLipSyncAsync(paths, job, plate, narration.AudioPath, cancellationToken)
                    .ConfigureAwait(false);

                if (repaired.HasSynchronizedMouth)
                {
                    Report("presenter_generated", $"Lip-sync applied via {repaired.Provider}", 0.60);
                    job.Capabilities.LipsyncRepair.Provider = repaired.Provider;
                    job.Capabilities.LipsyncRepair.Model = repaired.Model;
                    job.Capabilities.LipsyncRepair.TaskIds = repaired.TaskIds.ToList();
                    job.Capabilities.LipsyncRepair.Notes = "mouth timing replaced against the locked narration";
                    plate = repaired;
                }
            }

            if (!plate.HasSynchronizedMouth)
            {
                warnings.Add(
                    "The presenter track is an animated motion plate rendered from the still image, not a lip-synced talking head. Configure a lip-sync tool or a talking-head provider in Settings for synchronized mouth movement.");
            }

            if (options.Preview)
            {
                Report("rendered", "Rendering the preview composition", 0.70);
                var previewTimeline = await new TimelineBuilder(ffmpeg)
                    .BuildAsync(
                        paths, job, script, plate, narration, subtitlePath, cancellationToken,
                        captionPlan.Callouts,
                        job.Creative.PunchInsEnabled,
                        multiShotEnabled: false)
                    .ConfigureAwait(false);
                previewTimeline.Width = renderWidth;
                previewTimeline.Height = renderHeight;
                previewTimeline.FontsDirectory = string.IsNullOrWhiteSpace(settings.FontsDirectory)
                    ? string.Empty
                    : FileSystemUtil.ExpandPath(settings.FontsDirectory);

                Directory.CreateDirectory(paths.Renders);
                var previewPath = Path.Combine(paths.Renders, "preview.mp4");
                FileSystemUtil.TryDelete(previewPath);
                await new FfmpegCompositor(ffmpeg, Log)
                    .RenderAsync(previewTimeline, previewPath, cancellationToken, EncodeQuality.Preview)
                    .ConfigureAwait(false);

                _jobs.Save(paths, job);
                Report("rendered", "Preview ready", 1.0);

                return new PipelineResult
                {
                    Outcome = PipelineOutcome.Completed,
                    State = job.StateValue,
                    Message =
                        $"Preview rendered at {renderWidth}x{renderHeight}. Run again without preview mode for the delivery master.",
                    PreviewPath = previewPath,
                    DurationSeconds = previewTimeline.DurationSeconds,
                    Warnings = warnings,
                };
            }

            job.Capabilities.MainPresenter.Provider = generatorPlate.Provider;
            job.Capabilities.MainPresenter.Model = generatorPlate.Model;
            job.Capabilities.MainPresenter.TaskIds = generatorPlate.TaskIds.ToList();
            job.Capabilities.MainPresenter.Notes = generatorPlate.HasSynchronizedMouth
                ? "audio-driven talking head"
                : plate.HasSynchronizedMouth
                    ? $"animated motion plate with mouth timing supplied by {plate.Provider}"
                    : "animated motion plate; mouth is not audio-driven";
            job.Artifacts.PresenterPlate = paths.Relative(plate.Path);
            _jobs.Advance(paths, job, JobState.PresenterGenerated, $"presenter generated via {plate.Provider}");
            Report("presenter_generated", $"Presenter plate ready ({plate.DurationSeconds:0.00}s)", 0.62);

            // 6. Composition -----------------------------------------------------------
            Report("composition_checked", "Building the timeline", 0.66);
            var timeline = await new TimelineBuilder(ffmpeg)
                .BuildAsync(
                    paths, job, script, plate, narration, subtitlePath, cancellationToken,
                    captionPlan.Callouts,
                    job.Creative.PunchInsEnabled,
                    job.Creative.MultiShotEnabled)
                .ConfigureAwait(false);
            timeline.FontsDirectory = string.IsNullOrWhiteSpace(settings.FontsDirectory)
                ? string.Empty
                : FileSystemUtil.ExpandPath(settings.FontsDirectory);

            FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "TIMELINE.md"), timeline.ToMarkdown());
            FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "timeline.json"), JobJson.Serialize(timeline));
            job.Artifacts.Timeline = paths.Relative(Path.Combine(paths.Docs, "TIMELINE.md"));
            _jobs.Advance(paths, job, JobState.CompositionChecked,
                $"timeline built: {timeline.Clips.Count} clips and {timeline.PunchIns.Count} punch-ins over {timeline.DurationSeconds:0.000}s");

            Report("rendered", "Rendering the composition", 0.70);
            Directory.CreateDirectory(paths.Renders);
            var renderPath = Path.Combine(paths.Renders, "rendered.mkv");
            FileSystemUtil.TryDelete(renderPath);
            await new FfmpegCompositor(ffmpeg, Log)
                .RenderAsync(timeline, renderPath, cancellationToken)
                .ConfigureAwait(false);

            job.Artifacts.Rendered = paths.Relative(renderPath);
            _jobs.Advance(paths, job, JobState.Rendered, "composition rendered");
            Report("rendered", "Composition rendered", 0.84);

            // 7. Delivery and QA -------------------------------------------------------
            // Cards are joined onto the rendered program rather than cut into the timeline, so
            // every caption, callout and punch-in keeps the position the narration gave it. The
            // only thing that has to learn about the offset is the chapter list.
            var cards = await new CardService(ffmpeg, Log)
                .AttachAsync(paths, job, renderPath, cancellationToken)
                .ConfigureAwait(false);

            Report("verified", "Finalizing master and share encodes", 0.86);
            var stem = ResolveStem(options, job);
            var delivery = await new FinalizeDeliveryService(ffmpeg, Log)
                .RunAsync(
                    cards.Path,
                    paths.Outputs,
                    stem,
                    new FinalizeDeliveryService.Options
                    {
                        ProgramLufs = job.Voice.ProgramLufs,
                        Overwrite = true,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            job.Artifacts.Master = paths.Relative(Path.Combine(paths.Outputs, delivery.Master));
            job.Artifacts.Share = paths.Relative(Path.Combine(paths.Outputs, delivery.Share));
            job.Artifacts.ContactSheet = paths.Relative(Path.Combine(paths.Outputs, delivery.ContactSheet));
            job.Qa.DeliveryReport = paths.Relative(Path.Combine(paths.Outputs, $"{stem}-delivery-report.json"));

            Report("verified", "Running acceptance gates", 0.94);
            var qa = await new QaService(ffmpeg)
                .RunAsync(paths, job, timeline, delivery, asrReport, plate, cancellationToken, cards.TotalSeconds)
                .ConfigureAwait(false);

            var qaMarkdown = QaService.ToMarkdown(qa, delivery);
            var qaPath = Path.Combine(paths.QaReports, "DELIVERY-QA.md");
            FileSystemUtil.WriteAtomic(qaPath, qaMarkdown);
            job.Qa.ManualVisualReview = paths.Relative(qaPath);

            // Copy the sidecar captions next to the deliverables so a publisher finds them together.
            var srtSource = paths.Resolve(job.Artifacts.CaptionSrt);
            var srtDestination = Path.Combine(paths.Outputs, $"{stem}.srt");
            if (File.Exists(srtSource))
            {
                File.Copy(srtSource, srtDestination, overwrite: true);
            }

            // Extra aspect ratios reuse the locked narration and the accepted presenter plate;
            // only the frame geometry and the caption layout are rebuilt for each one.
            var alternates = new List<string>();
            foreach (var aspect in job.Creative.AdditionalAspects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var alternate = await RenderAlternateAspectAsync(
                            paths, job, script, plate, narration, captionPlan, settings, ffmpeg, aspect, stem,
                            Report, cancellationToken)
                        .ConfigureAwait(false);
                    alternates.Add(alternate);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    warnings.Add($"the {aspect} version failed: {exception.Message}");
                }
            }

            job.Artifacts.AlternateMasters = alternates.Select(paths.Relative).ToList();

            // Localization. Subtitles are cheap because the timings belong to the one recording
            // that was made; a dub is a full second run per language and is treated as such.
            if (job.Creative.SubtitleLanguages.Count > 0 || job.Creative.DubLanguages.Count > 0)
            {
                var translator = new TranslatorRouter(settings, _store, _httpClient).Resolve();
                if (translator is null)
                {
                    warnings.Add(
                        "no translation provider is configured, so no translated subtitles or dubs were produced; "
                        + "set an Anthropic or OpenAI key in Settings");
                }
                else
                {
                    if (job.Creative.SubtitleLanguages.Count > 0)
                    {
                        Report("verified", "Translating subtitles", 0.96);
                        var subtitles = await new SubtitleTranslationService(translator, Log)
                            .RunAsync(
                                paths, captionPlan, script.Language,
                                job.Creative.SubtitleLanguages, stem, cancellationToken)
                            .ConfigureAwait(false);

                        job.Artifacts.TranslatedSubtitles = subtitles.Files
                            .Select(file => paths.Relative(file.Path))
                            .ToList();
                        warnings.AddRange(subtitles.Warnings);

                        if (subtitles.Files.Count > 0)
                        {
                            Log($"Wrote {subtitles.Files.Count} translated subtitle file(s): "
                                + string.Join(", ", subtitles.Files.Select(file => file.Language)));
                        }
                    }

                    var dubs = new List<string>();
                    foreach (var language in job.Creative.DubLanguages
                                 .Select(entry => entry.Trim())
                                 .Where(entry => entry.Length > 0)
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var dub = await RenderDubAsync(
                                    paths, job, script, translator, settings, ffmpeg,
                                    language, stem, Report, Log, cancellationToken)
                                .ConfigureAwait(false);

                            dubs.Add(dub.MasterPath);
                            Log($"Dubbed into {language}: {dub.DurationSeconds:0.00}s "
                                + $"via {dub.VoiceProvider}, presenter {dub.PresenterProvider}");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            // One language failing must not cost the others or the delivery.
                            warnings.Add($"the {language} dub failed: {exception.Message}");
                        }
                    }

                    job.Artifacts.DubbedMasters = dubs.Select(paths.Relative).ToList();
                }
            }

            if (job.Creative.PublishingKit)
            {
                Report("verified", "Building thumbnails and chapter markers", 0.97);
                var kit = await new PublishingKitService(ffmpeg, Log)
                    .BuildAsync(
                        paths, job, script,
                        Path.Combine(paths.Outputs, delivery.CoverFrame),
                        stem, cancellationToken,
                        cleanSourceVideo: plate.Path,
                        programOffsetSeconds: cards.LeadSeconds)
                    .ConfigureAwait(false);

                job.Artifacts.Thumbnails = kit.Thumbnails.Select(paths.Relative).ToList();
                job.Artifacts.Chapters = paths.Relative(kit.ChaptersPath);
                job.Artifacts.Description = paths.Relative(kit.DescriptionPath);
                warnings.AddRange(kit.Notes);
            }

            _jobs.Advance(paths, job, JobState.Verified,
                qa.Ok ? "delivery verified" : "delivery finished with failing gates");

            Report("verified", qa.Ok ? "Delivery verified" : "Delivery finished with failing gates", 1.0);

            if (!qa.Ok)
            {
                warnings.AddRange(qa.Gates
                    .Where(gate => gate.Blocking && !gate.Passed)
                    .Select(gate => $"acceptance gate '{gate.Name}' failed: {gate.Detail}"));
            }

            warnings.AddRange(qa.ManualReviewRequired);

            return new PipelineResult
            {
                Outcome = PipelineOutcome.Completed,
                State = job.StateValue,
                Message = qa.Ok
                    ? "Master and share copies are ready. Watch the full video before publishing."
                    : "Rendered, but one or more acceptance gates failed. See the QA report.",
                MasterPath = Path.Combine(paths.Outputs, delivery.Master),
                SharePath = Path.Combine(paths.Outputs, delivery.Share),
                ContactSheetPath = Path.Combine(paths.Outputs, delivery.ContactSheet),
                AlternateMasters = alternates,
                Thumbnails = job.Artifacts.Thumbnails.Select(paths.Resolve).ToList(),
                CoverPath = Path.Combine(paths.Outputs, delivery.CoverFrame),
                CaptionsPath = File.Exists(srtDestination) ? srtDestination : string.Empty,
                DurationSeconds = delivery.DurationSeconds,
                QaPassed = qa.Ok,
                Warnings = warnings,
            };
        }
        catch (OperationCanceledException)
        {
            Log("run cancelled");
            _jobs.Save(paths, job);
            return new PipelineResult
            {
                Outcome = PipelineOutcome.Cancelled,
                State = job.StateValue,
                Message = "The run was cancelled. Re-running resumes from the last completed stage.",
            };
        }
        catch (Exception exception)
        {
            Log("FAILED: " + exception);
            job.Record("error", exception.Message);
            _jobs.Save(paths, job);
            return new PipelineResult
            {
                Outcome = PipelineOutcome.Failed,
                State = job.StateValue,
                Message = exception.Message,
            };
        }
    }

    private async Task<ScriptDocument> LoadOrWriteScriptAsync(
        JobPaths paths,
        JobManifest job,
        AppSettings settings,
        PipelineOptions options,
        Action<string, string, double> report,
        CancellationToken cancellationToken)
    {
        var scriptJsonPath = Path.Combine(paths.Docs, "script.json");

        if (!options.Force && File.Exists(scriptJsonPath) && job.StateValue >= JobState.ContentLocked)
        {
            report("content_locked", "Reusing the locked script", 0.10);
            return JobJson.Deserialize<ScriptDocument>(await File.ReadAllTextAsync(scriptJsonPath, cancellationToken).ConfigureAwait(false));
        }

        ScriptDocument script;
        if (!string.IsNullOrWhiteSpace(job.Input.ScriptPath) && File.Exists(job.Input.ScriptPath))
        {
            report("content_locked", "Reading the supplied script", 0.10);
            var text = await File.ReadAllTextAsync(job.Input.ScriptPath, cancellationToken).ConfigureAwait(false);
            script = ScriptDocument.FromSuppliedText(text, job.Input.Topic);
            if (script.Beats.Count == 0)
            {
                throw new InvalidDataException("the supplied script contains no narration");
            }
        }
        else
        {
            var writer = new ScriptWriterRouter(settings, _store, _httpClient).Resolve();
            report("content_locked", $"Writing the script with {writer.Name}", 0.10);

            var request = new ScriptRequest
            {
                Topic = job.Input.Topic,
                Language = job.Creative.Language,
                Audience = job.Creative.Audience,
                Goal = job.Creative.Goal,
                TargetSeconds = job.Creative.DurationTargetSeconds,
                Style = job.Creative.Style,
                Cta = job.Creative.Cta,
            };

            try
            {
                script = await writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (writer is not OutlineScriptWriter)
            {
                // A model outage must not strand the job; fall back and say so in the record.
                report("content_locked", $"{writer.Name} failed ({exception.Message}); using the built-in outline writer", 0.10);
                script = await new OutlineScriptWriter().WriteAsync(request, cancellationToken).ConfigureAwait(false);
                script.Notes.Add($"the {writer.Name} writer failed: {exception.Message}");
            }
        }

        if (job.Creative.Language is "auto" or "")
        {
            job.Creative.Language = script.Language;
        }

        if (!string.IsNullOrWhiteSpace(job.Creative.Cta)
            && !script.Narration.Contains(job.Creative.Cta, StringComparison.OrdinalIgnoreCase))
        {
            var close = script.Beats.LastOrDefault();
            if (close is not null)
            {
                close.Narration = close.Narration.TrimEnd() + " " + job.Creative.Cta.Trim();
            }
        }

        script.EnsureKeywords();
        FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "SCRIPT.md"), script.ToScriptMarkdown());
        FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "BEAT_SHEET.md"), script.ToBeatSheetMarkdown());
        FileSystemUtil.WriteAtomic(scriptJsonPath, JobJson.Serialize(script));

        job.Artifacts.Script = paths.Relative(Path.Combine(paths.Docs, "SCRIPT.md"));
        job.Artifacts.BeatSheet = paths.Relative(Path.Combine(paths.Docs, "BEAT_SHEET.md"));
        return script;
    }

    private async Task<(AsrVerificationReport Report, CaptionPlan Plan)> BuildCaptionsAsync(
        JobPaths paths,
        JobManifest job,
        AppSettings settings,
        ScriptDocument script,
        NarrationResult narration,
        FfmpegService ffmpeg,
        Action<string, string, double> report,
        CancellationToken cancellationToken)
    {
        var asrProvider = await new AsrRouter(settings, _store, _httpClient)
            .ResolveAsync(cancellationToken)
            .ConfigureAwait(false);

        AsrResult timings;
        var transcribed = false;

        if (asrProvider is not null)
        {
            report("visual_plan_locked", $"Transcribing the final audio with {asrProvider.Provider}", 0.39);
            try
            {
                var measured = await asrProvider
                    .TranscribeAsync(narration.AudioPath, job.Creative.Language, cancellationToken)
                    .ConfigureAwait(false);
                timings = ProportionalAligner.MergeMeasuredTimings(measured, narration.Segments);
                timings.Text = measured.Text;
                transcribed = true;
                job.Capabilities.WordTimestampAsr.Provider = asrProvider.Provider;
                job.Capabilities.WordTimestampAsr.Model = asrProvider.Model;
            }
            catch (Exception exception)
            {
                report("visual_plan_locked", $"Transcription failed ({exception.Message}); using the aligner", 0.39);
                timings = ProportionalAligner.Align(narration.Segments);
            }
        }
        else
        {
            report("visual_plan_locked", "No transcription service configured; using the proportional aligner", 0.39);
            timings = ProportionalAligner.Align(narration.Segments);
            job.Capabilities.WordTimestampAsr.Provider = "proportional-aligner";
            job.Capabilities.WordTimestampAsr.Model = "built-in";
        }

        var verification = AsrVerification.Compare(script.Narration, timings, transcribed);
        var asrPath = Path.Combine(paths.QaAsr, "verification.json");
        FileSystemUtil.WriteAtomic(asrPath, JobJson.Serialize(verification));
        FileSystemUtil.WriteAtomic(Path.Combine(paths.QaAsr, "VERIFICATION.md"), AsrVerification.ToMarkdown(verification));
        job.Qa.AsrReport = paths.Relative(asrPath);

        var plan = new CaptionBuilder().Build(
            timings,
            script,
            narration.Segments,
            job.Creative.KeywordCalloutsEnabled,
            new CaptionLayout(job.Creative.Width, job.Creative.Height));

        Directory.CreateDirectory(paths.Captions);
        var planPath = Path.Combine(paths.Captions, "captions.json");
        FileSystemUtil.WriteAtomic(planPath, JobJson.Serialize(plan));
        job.Artifacts.CaptionJson = paths.Relative(planPath);

        var srtPath = Path.Combine(paths.Captions, "captions.srt");
        FileSystemUtil.WriteAtomic(srtPath, SrtWriter.Build(plan));
        job.Artifacts.CaptionSrt = paths.Relative(srtPath);

        var assPath = Path.Combine(paths.Captions, "captions.ass");
        var styleOptions = new CaptionStyleOptions
        {
            Width = job.Creative.Width,
            Height = job.Creative.Height,
            FontName = string.IsNullOrWhiteSpace(job.Creative.CaptionFont)
                ? settings.CaptionFont
                : job.Creative.CaptionFont,
            AccentColor = job.Creative.AccentColor,
            Watermark = job.Creative.Watermark,
            CaptionsEnabled = job.Creative.CaptionsEnabled,
            CalloutsEnabled = job.Creative.KeywordCalloutsEnabled,
            CaptionStyle = job.Creative.CaptionStyle,
            Cjk = TextUtil.IsCjk(script.Narration),
        };

        FileSystemUtil.WriteAtomic(assPath, AssWriter.Build(plan, styleOptions));
        job.Artifacts.CaptionAss = paths.Relative(assPath);

        report("visual_plan_locked",
            $"{plan.Phrases.Count} caption phrases and {plan.Callouts.Count} keyword callouts",
            0.41);

        return (verification, plan);
    }

    private sealed record RemoteGate(PresenterPlate? Plate, PipelineResult? Blocked);

    /// <summary>
    /// Runs the paid path under the skill's billing and pilot gates: state the cost before the
    /// first call, generate the smallest useful pilot, and continue to the full run only after
    /// the pilot has been reviewed and approved.
    /// </summary>
    private async Task<RemoteGate> RunRemotePresenterAsync(
        JobPaths paths,
        JobManifest job,
        AppSettings settings,
        PresenterRoute route,
        NarrationResult narration,
        string platePath,
        Action<string, string, double> report,
        CancellationToken cancellationToken)
    {
        var remoteSettings = settings.Presenter.Remote;
        var pilotSeconds = Math.Min(6, Math.Max(3, narration.DurationSeconds * 0.1));

        if (!job.Plan.PaidGenerationApproved)
        {
            var disclosure = PresenterRouter.BuildCostDisclosure(
                job, remoteSettings, narration.DurationSeconds, pilotSeconds);
            job.Plan.EstimatedCost = remoteSettings.PricePerSecond * narration.DurationSeconds;
            job.Plan.PriceEvidenceDate = remoteSettings.PriceEvidenceDate;
            report("presenter_generated", "Waiting for paid-generation approval", 0.44);

            return new RemoteGate(null, new PipelineResult
            {
                Outcome = PipelineOutcome.NeedsApproval,
                State = job.StateValue,
                ApprovalKind = "paid_generation",
                ApprovalRequest = disclosure,
                Message = "Approve the paid generation plan to continue.",
            });
        }

        var pilotPath = Path.Combine(paths.VideoCandidates, "pilot.mp4");

        if (!job.Plan.PilotApproved)
        {
            if (!File.Exists(pilotPath))
            {
                report("presenter_generated", $"Generating a {pilotSeconds:0.0}s pilot", 0.46);
                var pilotAudio = Path.Combine(paths.Temp, "pilot-audio.wav");
                await TrimAudioAsync(narration.AudioPath, pilotAudio, pilotSeconds, cancellationToken)
                    .ConfigureAwait(false);

                await route.Generator.GenerateAsync(
                        new PresenterRequest
                        {
                            ImagePath = job.Input.PresenterImage,
                            AudioPath = pilotAudio,
                            OutputPath = pilotPath,
                            DurationSeconds = pilotSeconds,
                            Width = job.Creative.Width,
                            Height = job.Creative.Height,
                            Fps = job.Creative.Fps,
                            Prompt = PresenterPromptBuilder.Build(job, actionfulOpening: true),
                            NegativePrompt = PresenterPromptBuilder.NegativePrompt(),
                            IsPilot = true,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            report("presenter_generated", "Pilot ready for review", 0.50);
            return new RemoteGate(null, new PipelineResult
            {
                Outcome = PipelineOutcome.NeedsApproval,
                State = job.StateValue,
                ApprovalKind = "pilot",
                PilotPath = pilotPath,
                ApprovalRequest =
                    "Watch the pilot at normal speed and check identity, mouth timing, blinking, hands and lighting. "
                    + "Approve it to spend the full generation, or change the inputs and try again.",
                Message = "The pilot is ready for review.",
            });
        }

        var attempt = 0;
        Exception? lastFailure = null;

        while (attempt < Math.Max(1, job.Plan.RetryCeiling))
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();
            report("presenter_generated",
                $"Generating the full presenter take (attempt {attempt} of {job.Plan.RetryCeiling})",
                0.52);

            try
            {
                var plate = await route.Generator.GenerateAsync(
                        new PresenterRequest
                        {
                            ImagePath = job.Input.PresenterImage,
                            AudioPath = narration.AudioPath,
                            OutputPath = platePath,
                            DurationSeconds = narration.DurationSeconds,
                            Width = job.Creative.Width,
                            Height = job.Creative.Height,
                            Fps = job.Creative.Fps,
                            Prompt = PresenterPromptBuilder.Build(job, actionfulOpening: true),
                            NegativePrompt = PresenterPromptBuilder.NegativePrompt(),
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return new RemoteGate(plate, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                job.Plan.RejectedCandidates++;
                job.Record("presenter_generated", $"candidate {attempt} rejected: {exception.Message}");
                report("presenter_generated", $"Candidate {attempt} failed: {exception.Message}", 0.52);
            }
        }

        // Three rejected paid candidates: stop and summarize rather than keep spending.
        return new RemoteGate(null, new PipelineResult
        {
            Outcome = PipelineOutcome.Failed,
            State = job.StateValue,
            Message =
                $"Stopped after {job.Plan.RetryCeiling} rejected paid candidates. Last failure: {lastFailure?.Message}. "
                + "Candidates, prompts and task ids are preserved in qa/requests. Change one variable before retrying.",
        });
    }

    private static async Task TrimAudioAsync(
        string source,
        string destination,
        double seconds,
        CancellationToken cancellationToken)
    {
        var toolset = await MediaToolset.ResolveAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var ffmpeg = new FfmpegService(toolset);
        await ffmpeg.RunCheckedAsync(
                "pilot audio trim",
                new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                    "-i", source,
                    "-t", seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "1",
                    destination,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Produces one full dub: the script translated, spoken again in that language, re-timed
    /// against the new recording, and rendered and delivered as its own version.
    ///
    /// A dub is not a re-cut of the original. Every duration changes when the words change, so
    /// the chapters, captions, punch-ins, shot boundaries and the presenter plate itself are all
    /// rebuilt against the new narration rather than stretched to fit the old one.
    ///
    /// The presenter track is deliberately re-rendered as a local motion plate even when the
    /// original came from a paid provider. Generating a second talking head would be a second
    /// paid run, and quietly spending that on the operator's behalf is exactly what the cost gate
    /// exists to prevent — so the dub says which track it actually has instead.
    /// </summary>
    private async Task<DubResult> RenderDubAsync(
        JobPaths paths,
        JobManifest job,
        ScriptDocument script,
        ITranslator translator,
        AppSettings settings,
        FfmpegService ffmpeg,
        string language,
        string stem,
        Action<string, string, double> report,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var slug = SubtitleTranslationService.Slug(language);
        report("verified", $"Dubbing into {language}", 0.96);

        // 1. Translate every beat's narration, one line per beat so nothing merges or vanishes.
        var sourceLines = script.Beats.Select(beat => beat.Narration).ToList();
        var translatedLines = await translator
            .TranslateAsync(sourceLines, language, script.Language, cancellationToken)
            .ConfigureAwait(false);

        var dubScript = script.Clone();
        dubScript.Language = language;
        for (var index = 0; index < dubScript.Beats.Count; index++)
        {
            dubScript.Beats[index].Narration = translatedLines[index];
        }

        // 2. Speak it. A dub gets its own job view so narration reuse cannot hand it the
        //    original language's takes, which are cached by segment index.
        var dubPaths = paths.ForVariant(slug);
        var dubJob = job.CloneForDub(language);

        var router = new SpeechRouter(settings, _store, _httpClient);
        var synthesizer = await router.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var narration = await new NarrationService(ffmpeg, log)
            .BuildAsync(dubPaths, dubJob, dubScript, synthesizer, cancellationToken)
            .ConfigureAwait(false);

        log($"{language} narration is {narration.DurationSeconds:0.00}s against the original's {job.Plan.Chapters.Sum(chapter => chapter.DurationSeconds):0.00}s");

        // 3. Re-time. The words are different, so the timings have to be derived again.
        var (_, captionPlan) = await BuildCaptionsAsync(
                dubPaths, dubJob, settings, dubScript, narration, ffmpeg, report, cancellationToken)
            .ConfigureAwait(false);

        // 4. A new duration needs a new plate; the still image is the only thing carried over.
        var chapters = TimelineBuilder.BuildChapters(dubScript, narration);
        dubJob.Plan.Chapters = chapters;

        var envelope = settings.Presenter.Motion.AudioReactive
            ? await AudioEnvelope.MeasureAsync(
                    ffmpeg,
                    narration.AudioPath,
                    job.Creative.Fps,
                    (int)Math.Round(narration.DurationSeconds * job.Creative.Fps),
                    cancellationToken)
                .ConfigureAwait(false)
            : null;

        var platePath = Path.Combine(dubPaths.Renders, $"presenter-{slug}.mp4");
        var plate = await new MotionPlateGenerator(ffmpeg, settings.Presenter.Motion, log)
            .GenerateAsync(
                new PresenterRequest
                {
                    ImagePath = job.Input.PresenterImage,
                    AudioPath = narration.AudioPath,
                    OutputPath = platePath,
                    DurationSeconds = narration.DurationSeconds,
                    Width = job.Creative.Width,
                    Height = job.Creative.Height,
                    Fps = job.Creative.Fps,
                    Prompt = PresenterPromptBuilder.Build(dubJob, actionfulOpening: true),
                    NegativePrompt = PresenterPromptBuilder.NegativePrompt(),
                    Envelope = envelope,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // 5. Captions for this language, then the timeline and the render.
        var assPath = Path.Combine(dubPaths.Captions, $"captions-{slug}.ass");
        FileSystemUtil.WriteAtomic(assPath, AssWriter.Build(captionPlan, new CaptionStyleOptions
        {
            Width = job.Creative.Width,
            Height = job.Creative.Height,
            FontName = string.IsNullOrWhiteSpace(job.Creative.CaptionFont)
                ? settings.CaptionFont
                : job.Creative.CaptionFont,
            AccentColor = job.Creative.AccentColor,
            Watermark = job.Creative.Watermark,
            CaptionsEnabled = job.Creative.CaptionsEnabled,
            CalloutsEnabled = job.Creative.KeywordCalloutsEnabled,
            CaptionStyle = job.Creative.CaptionStyle,
            Cjk = TextUtil.IsCjk(dubScript.Narration),
        }));

        var timeline = await new TimelineBuilder(ffmpeg)
            .BuildAsync(
                dubPaths, dubJob, dubScript, plate, narration,
                job.Creative.CaptionsEnabled || job.Creative.KeywordCalloutsEnabled ? assPath : string.Empty,
                cancellationToken,
                captionPlan.Callouts,
                job.Creative.PunchInsEnabled,
                job.Creative.MultiShotEnabled)
            .ConfigureAwait(false);

        timeline.FontsDirectory = string.IsNullOrWhiteSpace(settings.FontsDirectory)
            ? string.Empty
            : FileSystemUtil.ExpandPath(settings.FontsDirectory);

        var renderPath = Path.Combine(dubPaths.Renders, $"rendered-{slug}.mkv");
        FileSystemUtil.TryDelete(renderPath);
        await new FfmpegCompositor(ffmpeg, log)
            .RenderAsync(timeline, renderPath, cancellationToken)
            .ConfigureAwait(false);

        var dubStem = $"{stem}-{slug}";
        var delivery = await new FinalizeDeliveryService(ffmpeg, log)
            .RunAsync(
                renderPath,
                paths.Outputs,
                dubStem,
                new FinalizeDeliveryService.Options
                {
                    ProgramLufs = job.Voice.ProgramLufs,
                    Overwrite = true,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // The sidecar belongs with the deliverable, in the dubbed language.
        var srtPath = Path.Combine(paths.Outputs, $"{dubStem}.srt");
        FileSystemUtil.WriteAtomic(srtPath, SrtWriter.Build(captionPlan));

        return new DubResult(
            language,
            Path.Combine(paths.Outputs, delivery.Master),
            narration.DurationSeconds,
            narration.Provider,
            plate.Provider);
    }

    private sealed record DubResult(
        string Language,
        string MasterPath,
        double DurationSeconds,
        string VoiceProvider,
        string PresenterProvider);

    /// <summary>
    /// Renders and finalizes one extra aspect ratio. The narration, presenter plate and caption
    /// timings are reused unchanged — only the frame geometry and the caption layout differ, so
    /// every version stays on the same clock.
    /// </summary>
    private async Task<string> RenderAlternateAspectAsync(
        JobPaths paths,
        JobManifest job,
        ScriptDocument script,
        PresenterPlate plate,
        NarrationResult narration,
        CaptionPlan captionPlan,
        AppSettings settings,
        FfmpegService ffmpeg,
        string aspect,
        string stem,
        Action<string, string, double> report,
        CancellationToken cancellationToken)
    {
        var (width, height) = JobService.AspectDefaults[aspect];
        var slug = aspect.Replace(':', 'x');
        report("verified", $"Rendering the {aspect} version", 0.95);

        // Callouts and captions sit in different safe regions per orientation, so the subtitle
        // file is rebuilt for these dimensions rather than scaled from the delivery one.
        var assPath = Path.Combine(paths.Captions, $"captions-{slug}.ass");
        FileSystemUtil.WriteAtomic(assPath, AssWriter.Build(captionPlan, new CaptionStyleOptions
        {
            Width = width,
            Height = height,
            FontName = string.IsNullOrWhiteSpace(job.Creative.CaptionFont)
                ? settings.CaptionFont
                : job.Creative.CaptionFont,
            AccentColor = job.Creative.AccentColor,
            Watermark = job.Creative.Watermark,
            CaptionsEnabled = job.Creative.CaptionsEnabled,
            CalloutsEnabled = job.Creative.KeywordCalloutsEnabled,
            CaptionStyle = job.Creative.CaptionStyle,
            Cjk = TextUtil.IsCjk(script.Narration),
        }));

        var timeline = await new TimelineBuilder(ffmpeg)
            .BuildAsync(
                paths, job, script, plate, narration,
                job.Creative.CaptionsEnabled || job.Creative.KeywordCalloutsEnabled ? assPath : string.Empty,
                cancellationToken,
                captionPlan.Callouts,
                job.Creative.PunchInsEnabled,
                job.Creative.MultiShotEnabled)
            .ConfigureAwait(false);

        timeline.Width = width;
        timeline.Height = height;
        timeline.FontsDirectory = string.IsNullOrWhiteSpace(settings.FontsDirectory)
            ? string.Empty
            : FileSystemUtil.ExpandPath(settings.FontsDirectory);

        var renderPath = Path.Combine(paths.Renders, $"rendered-{slug}.mkv");
        FileSystemUtil.TryDelete(renderPath);
        await new FfmpegCompositor(ffmpeg)
            .RenderAsync(timeline, renderPath, cancellationToken)
            .ConfigureAwait(false);

        var alternateStem = $"{stem}-{slug}";
        var alternateDelivery = await new FinalizeDeliveryService(ffmpeg)
            .RunAsync(
                renderPath,
                paths.Outputs,
                alternateStem,
                new FinalizeDeliveryService.Options
                {
                    ProgramLufs = job.Voice.ProgramLufs,
                    Overwrite = true,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return Path.Combine(paths.Outputs, alternateDelivery.Master);
    }

    /// <summary>
    /// Scales the delivery frame down to the requested short edge, keeping the aspect ratio and
    /// forcing even dimensions so H.264 accepts them.
    /// </summary>
    internal static (int Width, int Height) ProxyDimensions(int width, int height, int targetShortEdge)
    {
        var shortEdge = Math.Min(width, height);
        var target = Math.Clamp(targetShortEdge, 160, shortEdge);
        var scale = (double)target / shortEdge;

        var proxyWidth = Math.Max(2, (int)Math.Round(width * scale));
        var proxyHeight = Math.Max(2, (int)Math.Round(height * scale));

        if (proxyWidth % 2 != 0)
        {
            proxyWidth++;
        }

        if (proxyHeight % 2 != 0)
        {
            proxyHeight++;
        }

        return (proxyWidth, proxyHeight);
    }

    private static string ResolveStem(PipelineOptions options, JobManifest job)
    {
        var candidate = string.IsNullOrWhiteSpace(options.OutputStem) ? job.JobId : options.OutputStem;
        candidate = FileSystemUtil.Slugify(candidate);
        return FileSystemUtil.IsSafeStem(candidate) ? candidate : "presenter-video";
    }
}
