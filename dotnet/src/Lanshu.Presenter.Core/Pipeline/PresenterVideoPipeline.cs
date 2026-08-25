using System.Text;
using Lanshu.Presenter.Core.Asr;
using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Delivery;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Jobs;
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
            Report("environment", $"Using {Path.GetFileName(toolset.FfmpegPath)} — {toolset.Version}", 0.02);

            job.Capabilities.EncoderQa.Provider = "ffmpeg";
            job.Capabilities.EncoderQa.Version = toolset.Version;
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
            var platePath = Path.Combine(plateDirectory, "presenter.mp4");

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

                warnings.Add(
                    "The presenter track is an animated motion plate rendered from the still image, not a lip-synced talking head. Configure a talking-head provider in Settings for synchronized mouth movement.");
            }

            job.Capabilities.MainPresenter.Provider = plate.Provider;
            job.Capabilities.MainPresenter.Model = plate.Model;
            job.Capabilities.MainPresenter.TaskIds = plate.TaskIds.ToList();
            job.Capabilities.MainPresenter.Notes = plate.HasSynchronizedMouth
                ? "audio-driven talking head"
                : "animated motion plate; mouth is not audio-driven";
            job.Artifacts.PresenterPlate = paths.Relative(plate.Path);
            _jobs.Advance(paths, job, JobState.PresenterGenerated, $"presenter generated via {plate.Provider}");
            Report("presenter_generated", $"Presenter plate ready ({plate.DurationSeconds:0.00}s)", 0.62);

            // 6. Composition -----------------------------------------------------------
            Report("composition_checked", "Building the timeline", 0.66);
            var subtitlePath = job.Creative.CaptionsEnabled || job.Creative.KeywordCalloutsEnabled
                ? paths.Resolve(job.Artifacts.CaptionAss)
                : string.Empty;

            var timeline = await new TimelineBuilder(ffmpeg)
                .BuildAsync(paths, job, script, plate, narration, subtitlePath, cancellationToken)
                .ConfigureAwait(false);
            timeline.FontsDirectory = string.IsNullOrWhiteSpace(settings.FontsDirectory)
                ? string.Empty
                : FileSystemUtil.ExpandPath(settings.FontsDirectory);

            FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "TIMELINE.md"), timeline.ToMarkdown());
            FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "timeline.json"), JobJson.Serialize(timeline));
            job.Artifacts.Timeline = paths.Relative(Path.Combine(paths.Docs, "TIMELINE.md"));
            _jobs.Advance(paths, job, JobState.CompositionChecked,
                $"timeline built: {timeline.Clips.Count} clips over {timeline.DurationSeconds:0.000}s");

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
            Report("verified", "Finalizing master and share encodes", 0.86);
            var stem = ResolveStem(options, job);
            var delivery = await new FinalizeDeliveryService(ffmpeg, Log)
                .RunAsync(
                    renderPath,
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
                .RunAsync(paths, job, timeline, delivery, asrReport, plate, cancellationToken)
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

    private static string ResolveStem(PipelineOptions options, JobManifest job)
    {
        var candidate = string.IsNullOrWhiteSpace(options.OutputStem) ? job.JobId : options.OutputStem;
        candidate = FileSystemUtil.Slugify(candidate);
        return FileSystemUtil.IsSafeStem(candidate) ? candidate : "presenter-video";
    }
}
