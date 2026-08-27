using System.Globalization;
using Lanshu.Presenter.Cli;
using Lanshu.Presenter.Core.Branding;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Pipeline;
using Lanshu.Presenter.Core.Preflight;
using Lanshu.Presenter.Core.Presenter;
using Lanshu.Presenter.Core.Util;
using Lanshu.Presenter.Core.Voice;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var line = new CommandLine(args.Skip(1));
using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
var store = new SettingsStore();

try
{
    return command switch
    {
        "init" => await InitAsync(),
        "preflight" => await PreflightAsync(),
        "run" => await RunAsync(),
        "approve" => Approve(),
        "finalize" => await FinalizeAsync(),
        "segments" => Segments(),
        "retake" => Retake(),
        "pilot" => await PilotAsync(),
        "lipsync" => await LipSyncAsync(),
        "brand" => Brand(),
        "broll" => BRoll(),
        "doctor" => await DoctorAsync(),
        "voices" => await VoicesAsync(),
        "jobs" => JobsList(),
        "config" => Config(),
        "version" => Version(),
        _ => Help(command is "help" or "--help" or "-h" ? 0 : 64),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("cancelled");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine("ERROR: " + exception.Message);
    return 2;
}

async Task<int> InitAsync()
{
    var settings = store.Load();
    var jobDirectory = line.Value("job-dir")
        ?? Path.Combine(settings.ResolvedWorkspace, FileSystemUtil.Slugify(
            line.Value("topic") ?? Path.GetFileNameWithoutExtension(line.Value("script") ?? "presenter-video"))
            + "-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmm"));

    var request = new NewJobRequest
    {
        JobDirectory = jobDirectory,
        PresenterImage = line.Value("presenter-image")
                         ?? throw new ArgumentException("--presenter-image is required"),
        Topic = line.Value("topic"),
        ScriptFile = line.Value("script"),
        VoiceSample = line.Value("voice-sample"),
        SupportingMedia = line.Values("supporting-media"),
        Language = line.Value("language") ?? settings.Defaults.Language,
        Audience = line.Value("audience") ?? "general",
        DurationSeconds = line.Number("duration", settings.Defaults.DurationTargetSeconds),
        Aspect = line.Value("aspect") ?? settings.Defaults.Aspect,
        Width = line.Has("width") ? line.Integer("width", 0) : null,
        Height = line.Has("height") ? line.Integer("height", 0) : null,
        Fps = line.Integer("fps", settings.Defaults.Fps),
        Style = line.Value("style") ?? settings.Defaults.Style,
        Watermark = line.Value("watermark") ?? string.Empty,
        Cta = line.Value("cta") ?? string.Empty,
        AccentColor = line.Value("accent") ?? settings.Defaults.AccentColor,
        MusicPath = line.Value("music"),
        CaptionsEnabled = !line.Flag("no-captions"),
        KeywordCalloutsEnabled = !line.Flag("no-callouts"),
        PunchInsEnabled = !line.Flag("no-punch-ins"),
        MultiShotEnabled = !line.Flag("no-multi-shot"),
        PublishingKit = !line.Flag("no-publishing-kit"),
        AdditionalAspects = line.Values("also-aspect"),
        RightsConfirmed = line.Flag("rights-confirmed"),
        AdultPresenterConfirmed = line.Flag("adult-presenter-confirmed"),
        RemoteUploadApproved = line.Flag("remote-upload-approved"),
        VoiceCloneApproved = line.Flag("voice-clone-approved"),
        ManualReview = line.Flag("reviewed")
            ? new ManualInputReview
            {
                ImageViewed = true,
                SingleClearFace = true,
                ImageHasNoUnwantedText = true,
                VoiceSampleListened = !string.IsNullOrWhiteSpace(line.Value("voice-sample")),
                SingleClearSpeaker = !string.IsNullOrWhiteSpace(line.Value("voice-sample")),
            }
            : null,
    };

    var paths = new JobService().Create(request);

    // Everything below is presentation the NewJobRequest does not carry. A brand kit is applied
    // first so an explicit flag on the same command line still wins over the kit.
    if (line.Flag("review-script")
        || line.Has("brand")
        || line.Has("caption-style")
        || line.Has("intro")
        || line.Has("outro")
        || line.Flag("no-trim-silence")
        || line.Flag("keep-fillers"))
    {
        var jobs = new JobService();
        var manifest = jobs.Load(paths);

        if (line.Flag("review-script"))
        {
            manifest.Plan.ReviewScript = true;
        }

        if (line.Has("brand"))
        {
            var brand = line.Value("brand");
            if (!new BrandKitService(store).Apply(brand ?? string.Empty, manifest.Creative))
            {
                Console.Error.WriteLine($"WARNING: no brand kit named '{brand}'; the job keeps its own look.");
            }
        }

        if (line.Has("caption-style"))
        {
            manifest.Creative.CaptionStyle = line.Value("caption-style") ?? manifest.Creative.CaptionStyle;
        }

        if (line.Has("intro"))
        {
            manifest.Creative.Intro.Enabled = true;
            manifest.Creative.Intro.Title = line.Value("intro") ?? string.Empty;
            manifest.Creative.Intro.Subtitle = line.Value("intro-subtitle") ?? manifest.Creative.Intro.Subtitle;
        }

        if (line.Has("outro"))
        {
            manifest.Creative.Outro.Enabled = true;
            manifest.Creative.Outro.Title = line.Value("outro") ?? string.Empty;
            manifest.Creative.Outro.Subtitle = line.Value("outro-subtitle") ?? manifest.Creative.Outro.Subtitle;
        }

        if (line.Flag("no-trim-silence"))
        {
            manifest.Creative.TrimSilence = false;
            manifest.Voice.SilenceTrimmed = false;
        }

        if (line.Flag("keep-fillers"))
        {
            manifest.Creative.TrimFillers = false;
        }

        jobs.Save(paths, manifest);
    }

    Console.WriteLine(JobJson.Serialize(new { job = paths.ManifestFile, state = "intake" }).TrimEnd());

    if (request.ManualReview is null)
    {
        Console.WriteLine();
        Console.WriteLine("Next: look at the presenter image yourself, then record the review and approvals in job.json,");
        Console.WriteLine("or re-run init with --reviewed --rights-confirmed --adult-presenter-confirmed.");
    }

    return await Task.FromResult(0);
}

async Task<int> PreflightAsync()
{
    var paths = ResolvePaths();
    var jobs = new JobService();
    var job = jobs.Load(paths);
    var settings = store.Load();
    var toolset = await MediaToolset.ResolveAsync(settings.FfmpegPath, settings.FfprobePath);
    var report = await new PreflightService(new FfmpegService(toolset)).RunAsync(paths, job);
    jobs.Save(paths, job);

    Console.WriteLine(JobJson.Serialize(report).TrimEnd());
    return report.Ok ? 0 : 1;
}

async Task<int> RunAsync()
{
    var paths = ResolvePaths();
    var pipeline = new PresenterVideoPipeline(store, httpClient);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var progress = new Progress<PipelineEvent>(item =>
        Console.WriteLine($"[{item.Progress * 100,5:0.0}%] {item.Stage,-20} {item.Message}"));

    var result = await pipeline.RunAsync(
        paths,
        new PipelineOptions
        {
            AudioOnly = line.Flag("audio-only"),
            Preview = line.Flag("preview"),
            PreviewHeight = line.Integer("preview-height", 0),
            Force = line.Flag("force"),
            Overwrite = true,
            OutputStem = line.Value("stem") ?? string.Empty,
        },
        progress,
        cancellation.Token);

    Console.WriteLine();
    Console.WriteLine(result.Message);

    foreach (var warning in result.Warnings)
    {
        Console.WriteLine("  ! " + warning);
    }

    switch (result.Outcome)
    {
        case PipelineOutcome.NeedsApproval:
            Console.WriteLine();
            Console.WriteLine(result.ApprovalRequest);
            Console.WriteLine();
            if (result.ApprovalKind == "script")
            {
                Console.WriteLine("Edit the narration in: " + Path.Combine(paths.Docs, "script.json"));
            }

            if (result.ApprovalKind == "pilot")
            {
                Console.WriteLine($"Review it with: lanshu pilot --job-dir \"{paths.Root}\" [--open]");
            }

            Console.WriteLine($"Approve with: lanshu approve --job-dir \"{paths.Root}\" --{result.ApprovalKind.Replace('_', '-')}");
            return 3;

        case PipelineOutcome.Completed when !string.IsNullOrEmpty(result.PreviewPath):
            Console.WriteLine();
            Console.WriteLine("Preview: " + result.PreviewPath);
            return 0;

        case PipelineOutcome.Completed:
            Console.WriteLine();
            Console.WriteLine("Master:  " + result.MasterPath);
            Console.WriteLine("Share:   " + result.SharePath);
            Console.WriteLine("Cover:   " + result.CoverPath);
            Console.WriteLine("Contact: " + result.ContactSheetPath);
            foreach (var alternate in result.AlternateMasters)
            {
                Console.WriteLine("Also:    " + alternate);
            }
            return result.QaPassed ? 0 : 1;

        case PipelineOutcome.Cancelled:
            return 130;

        default:
            return 2;
    }
}

int Approve()
{
    var paths = ResolvePaths();
    var jobs = new JobService();
    var job = jobs.Load(paths);

    if (line.Flag("paid-generation"))
    {
        job.Plan.PaidGenerationApproved = true;
        job.Record("plan", "paid generation approved");
        Console.WriteLine("Paid generation approved.");
    }

    if (line.Flag("script"))
    {
        job.Plan.ScriptApproved = true;
        job.Record("plan", "script approved");
        Console.WriteLine("Script approved.");
    }

    if (line.Flag("pilot"))
    {
        job.Plan.PilotApproved = true;
        job.Record("plan", "pilot approved");
        Console.WriteLine("Pilot approved.");
    }

    if (line.Flag("rights"))
    {
        job.Input.RightsConfirmed = true;
        job.Input.AdultPresenterConfirmed = true;
        Console.WriteLine("Image rights and adult presenter status confirmed.");
    }

    if (line.Flag("remote-upload"))
    {
        job.Input.RemoteUploadApproved = true;
        Console.WriteLine("Remote upload approved.");
    }

    if (line.Flag("reviewed"))
    {
        job.ManualInputReview.ImageViewed = true;
        job.ManualInputReview.SingleClearFace = true;
        job.ManualInputReview.ImageHasNoUnwantedText = true;
        Console.WriteLine("Manual input review recorded.");
    }

    jobs.Save(paths, job);
    return 0;
}

async Task<int> FinalizeAsync()
{
    var input = line.Value("input") ?? line.Positional.ElementAtOrDefault(0)
        ?? throw new ArgumentException("--input rendered.mp4 is required");
    var outputDirectory = line.Value("output") ?? line.Positional.ElementAtOrDefault(1)
        ?? throw new ArgumentException("--output <directory> is required");
    var stem = line.Value("stem") ?? line.Positional.ElementAtOrDefault(2) ?? "presenter-video";

    var settings = store.Load();
    var toolset = await MediaToolset.ResolveAsync(settings.FfmpegPath, settings.FfprobePath);
    var ffmpeg = new FfmpegService(toolset, message => Console.Error.WriteLine(message));

    var report = await new Lanshu.Presenter.Core.Delivery.FinalizeDeliveryService(
            ffmpeg,
            Console.WriteLine)
        .RunAsync(
            FileSystemUtil.ExpandPath(input),
            FileSystemUtil.ExpandPath(outputDirectory),
            stem,
            new Lanshu.Presenter.Core.Delivery.FinalizeDeliveryService.Options
            {
                ProgramLufs = line.Number("lufs", settings.Defaults.ProgramLufs),
                Overwrite = line.Flag("overwrite"),
            });

    Console.WriteLine(JobJson.Serialize(report).TrimEnd());
    return 0;
}

int Segments()
{
    var paths = ResolvePaths();
    var job = new JobService().Load(paths);

    if (job.Voice.Sections.Count == 0)
    {
        Console.WriteLine("This job has no narration segments yet. Run it once first.");
        return 0;
    }

    Console.WriteLine($"{"#",3}  {"START",8}  {"LENGTH",7}  TEXT");
    foreach (var section in job.Voice.Sections.OrderBy(section => section.Index))
    {
        Console.WriteLine(
            $"{section.Index,3}  {section.StartSeconds,8:0.00}  {section.DurationSeconds,7:0.00}  "
            + Truncate(section.Text, 68));

        if (!string.IsNullOrWhiteSpace(section.SpokenOverride))
        {
            Console.WriteLine($"{string.Empty,3}  {string.Empty,8}  {string.Empty,7}  spoken as: {Truncate(section.SpokenOverride, 58)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("Re-take one with:  lanshu retake --job-dir <dir> --index <n> [--say \"respelling\"]");
    return 0;
}

int Brand()
{
    var service = new BrandKitService(store);

    if (line.Has("delete"))
    {
        var name = line.Value("delete") ?? string.Empty;
        Console.WriteLine(service.Delete(name)
            ? $"Deleted the brand kit '{name}'."
            : $"No brand kit named '{name}'.");
        return service.Find(name) is null ? 0 : 2;
    }

    if (line.Has("save"))
    {
        var name = line.Value("save") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("--save needs a name for the kit.");
            return 64;
        }

        // A kit captured from a real job is worth more than one typed from scratch: the job
        // already holds a look someone looked at and accepted.
        var kit = line.Has("job-dir") || line.Has("job")
            ? BrandKitService.FromCreative(name, new JobService().Load(ResolvePaths()).Creative)
            : new BrandKit { Name = name };

        if (line.Has("accent")) kit.AccentColor = line.Value("accent") ?? kit.AccentColor;
        if (line.Has("font")) kit.CaptionFont = line.Value("font") ?? kit.CaptionFont;
        if (line.Has("caption-style")) kit.CaptionStyle = line.Value("caption-style") ?? kit.CaptionStyle;
        if (line.Has("watermark")) kit.Watermark = line.Value("watermark") ?? kit.Watermark;
        if (line.Has("intro")) { kit.Intro.Enabled = true; kit.Intro.Title = line.Value("intro") ?? string.Empty; }
        if (line.Has("intro-subtitle")) kit.Intro.Subtitle = line.Value("intro-subtitle") ?? kit.Intro.Subtitle;
        if (line.Has("outro")) { kit.Outro.Enabled = true; kit.Outro.Title = line.Value("outro") ?? string.Empty; }
        if (line.Has("outro-subtitle")) kit.Outro.Subtitle = line.Value("outro-subtitle") ?? kit.Outro.Subtitle;
        if (line.Has("logo")) { kit.Intro.LogoPath = line.Value("logo") ?? string.Empty; kit.Outro.LogoPath = kit.Intro.LogoPath; }

        var replaced = service.Save(kit);
        Console.WriteLine($"{(replaced ? "Updated" : "Saved")} the brand kit '{kit.Name}'.");
        Console.WriteLine($"Use it with:  lanshu init --brand {kit.Name}");
        return 0;
    }

    var kits = service.List();
    if (kits.Count == 0)
    {
        Console.WriteLine("No brand kits yet.");
        Console.WriteLine();
        Console.WriteLine("Save the look of a job you liked:");
        Console.WriteLine("  lanshu brand --save house --job-dir <dir>");
        Console.WriteLine("Or start one from scratch:");
        Console.WriteLine("  lanshu brand --save house --accent \"#F4C430\" --caption-style tiktok");
        return 0;
    }

    Console.WriteLine($"{"NAME",-16}  {"ACCENT",-9}  {"CAPTIONS",-9}  CARDS");
    foreach (var kit in kits)
    {
        var cards = (kit.Intro.Enabled ? "intro" : string.Empty)
                    + (kit.Intro.Enabled && kit.Outro.Enabled ? "+" : string.Empty)
                    + (kit.Outro.Enabled ? "outro" : string.Empty);
        Console.WriteLine($"{kit.Name,-16}  {kit.AccentColor,-9}  {kit.CaptionStyle,-9}  {(cards.Length == 0 ? "-" : cards)}");
    }

    return 0;
}

int BRoll()
{
    var paths = ResolvePaths();
    var jobs = new JobService();
    var job = jobs.Load(paths);

    if (job.Plan.Chapters.Count == 0)
    {
        Console.WriteLine("This job has no chapters yet, so there is nothing to assign media to.");
        Console.WriteLine("Run it once — chapters are measured from the narration, not guessed.");
        return 0;
    }

    if (line.Has("clear"))
    {
        var chapter = line.Integer("clear", -1);
        job.Plan.InsertAssignments.RemoveAll(entry => entry.ChapterIndex == chapter);
        // An explicit empty assignment is how "leave this chapter clean" survives the next run;
        // removing the entry entirely would hand the chapter back to round-robin.
        job.Plan.InsertAssignments.Add(new InsertAssignment { ChapterIndex = chapter, Media = string.Empty });
        jobs.Save(paths, job);
        Console.WriteLine($"Chapter {chapter} will stay clean.");
        return 0;
    }

    if (line.Has("set"))
    {
        var pair = line.Value("set") ?? string.Empty;
        var split = pair.IndexOf('=');
        if (split <= 0)
        {
            Console.Error.WriteLine("--set wants <chapter>=<file>, for example --set 2=diagram.png");
            return 64;
        }

        if (!int.TryParse(pair[..split], out var chapter)
            || chapter < 0
            || chapter >= job.Plan.Chapters.Count)
        {
            Console.Error.WriteLine($"chapter must be between 0 and {job.Plan.Chapters.Count - 1}");
            return 64;
        }

        var media = pair[(split + 1)..].Trim();
        var known = job.Input.SupportingMedia.FirstOrDefault(candidate =>
            string.Equals(candidate, media, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(candidate), media, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            Console.Error.WriteLine($"'{media}' is not one of this job's supporting files.");
            Console.Error.WriteLine("Add it with 'lanshu init --media <file>' first.");
            return 64;
        }

        job.Plan.InsertAssignments.RemoveAll(entry => entry.ChapterIndex == chapter);
        job.Plan.InsertAssignments.Add(new InsertAssignment
        {
            ChapterIndex = chapter,
            Media = known,
            OffsetSeconds = line.Has("at") ? line.Number("at", -1) : -1,
            DurationSeconds = line.Number("for", 0),
        });

        jobs.Save(paths, job);
        Console.WriteLine($"Chapter {chapter} will show {Path.GetFileName(known)}.");
        Console.WriteLine("Run the job again to rebuild the timeline.");
        return 0;
    }

    var assignments = job.Plan.InsertAssignments.ToDictionary(entry => entry.ChapterIndex);
    Console.WriteLine($"{"#",3}  {"START",8}  {"LENGTH",7}  {"CHAPTER",-26}  MEDIA");
    foreach (var chapter in job.Plan.Chapters.OrderBy(chapter => chapter.Index))
    {
        assignments.TryGetValue(chapter.Index, out var assignment);
        var media = assignment is null
            ? (string.IsNullOrWhiteSpace(chapter.SupportingMedia)
                ? "-"
                : Path.GetFileName(chapter.SupportingMedia) + "  (auto)")
            : (string.IsNullOrWhiteSpace(assignment.Media)
                ? "(kept clean)"
                : Path.GetFileName(assignment.Media));

        Console.WriteLine(
            $"{chapter.Index,3}  {chapter.StartSeconds,8:0.00}  {chapter.DurationSeconds,7:0.00}  "
            + $"{Truncate(chapter.Title, 26),-26}  {media}");
    }

    Console.WriteLine();
    if (job.Plan.InsertAssignments.Count == 0)
    {
        Console.WriteLine("Nothing is assigned by hand, so media is placed round-robin over the body chapters.");
    }

    Console.WriteLine("Assign one with:  lanshu broll --set 2=diagram.png [--at 0.5] [--for 3]");
    Console.WriteLine("Keep one clean:   lanshu broll --clear 2");
    return 0;
}

int Retake()
{
    var paths = ResolvePaths();
    var jobs = new JobService();
    var job = jobs.Load(paths);

    if (!line.Has("index"))
    {
        Console.Error.WriteLine("--index <n> is required. Run 'lanshu segments' to see them.");
        return 64;
    }

    var index = line.Integer("index", -1);
    var spoken = line.Has("clear-say") ? string.Empty : line.Value("say");

    var result = new SegmentRetakeService().Request(paths, job, index, spoken);

    Console.WriteLine($"Segment {result.Index} will be spoken again on the next run.");
    Console.WriteLine($"  script: {result.Text}");
    if (!string.Equals(result.Text, result.SpokenText, StringComparison.Ordinal))
    {
        Console.WriteLine($"  spoken: {result.SpokenText}");
        Console.WriteLine("  (captions keep the script wording)");
    }

    Console.WriteLine();
    Console.WriteLine("Every other segment keeps its existing audio. Run the job to rebuild.");
    return 0;
}

async Task<int> PilotAsync()
{
    var paths = ResolvePaths();
    var job = new JobService().Load(paths);
    var pilot = Path.Combine(paths.VideoCandidates, "pilot.mp4");

    if (!File.Exists(pilot))
    {
        Console.WriteLine("No pilot has been generated for this job yet.");
        Console.WriteLine("A pilot is produced before the first full paid presenter run.");
        return 1;
    }

    var settings = store.Load();
    var toolset = await MediaToolset.ResolveAsync(settings.FfmpegPath, settings.FfprobePath);
    var ffmpeg = new FfmpegService(toolset);
    var probe = await ffmpeg.ProbeAsync(pilot);
    var video = probe.Video;

    Console.WriteLine("Pilot: " + pilot);
    Console.WriteLine($"  duration : {probe.DurationSeconds:0.00}s");
    if (video is not null)
    {
        Console.WriteLine($"  format   : {video.Width}x{video.Height} @ {video.FrameRate:0.##}fps, {video.CodecName}");
    }

    Console.WriteLine($"  audio    : {(probe.HasAudio ? "present" : "none")}");
    Console.WriteLine($"  provider : {job.Capabilities.MainPresenter.Provider}");

    // A terminal cannot play video, so lay the pilot out as frames that can be opened as one image.
    var sheet = Path.Combine(paths.QaContacts, "pilot-contact-sheet.png");
    try
    {
        await BuildPilotSheetAsync(ffmpeg, pilot, probe.DurationSeconds, sheet);
        Console.WriteLine("  frames   : " + sheet);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("  (could not build a contact sheet: " + exception.Message + ")");
    }

    if (line.Flag("open"))
    {
        OpenInDefaultApplication(pilot);
    }

    Console.WriteLine();
    Console.WriteLine("Check identity, mouth timing, blinking, hands and lighting at normal speed.");
    Console.WriteLine($"Approve with: lanshu approve --job-dir \"{paths.Root}\" --pilot");
    return 0;
}

async Task BuildPilotSheetAsync(FfmpegService ffmpeg, string pilot, double duration, string destination)
{
    var scratch = Path.Combine(Path.GetTempPath(), $"lanshu-pilot-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratch);

    try
    {
        for (var index = 0; index < 6; index++)
        {
            var timestamp = duration * ((index + 0.5) / 6.0);
            await ffmpeg.ExtractFrameAsync(
                pilot,
                Math.Max(0, timestamp),
                Path.Combine(scratch, $"f-{index + 1:00}.png"),
                "scale=320:-2:flags=lanczos");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tile = Path.Combine(scratch, "sheet.png");
        await ffmpeg.RunCheckedAsync(
            "pilot contact sheet",
            new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-framerate", "1", "-start_number", "1",
                "-i", Path.Combine(scratch, "f-%02d.png"),
                "-frames:v", "1",
                "-vf", "tile=3x2:padding=10:margin=10:color=0x101218",
                "-update", "1",
                tile,
            });

        File.Move(tile, destination, overwrite: true);
    }
    finally
    {
        FileSystemUtil.TryDeleteDirectory(scratch);
    }
}

void OpenInDefaultApplication(string path)
{
    try
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
    catch (Exception)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("xdg-open", path))?.Dispose();
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Could not open the file; the path is printed above.");
        }
    }
}

static string Truncate(string text, int maxLength) =>
    text.Length <= maxLength ? text : text[..(maxLength - 1)] + "\u2026";

async Task<int> LipSyncAsync()
{
    var settings = store.Load();
    var toolset = await MediaToolset.ResolveAsync(settings.FfmpegPath, settings.FfprobePath);
    var service = new LipSyncSetupService(store, new FfmpegService(toolset), Console.WriteLine);

    if (line.Flag("list"))
    {
        Console.WriteLine("Presets. Install the tool yourself, then point the studio at your checkout.");
        Console.WriteLine();
        foreach (var preset in LipSyncSetupService.Presets)
        {
            Console.WriteLine($"  {preset.Id,-16} {preset.DisplayName}");
            Console.WriteLine($"  {string.Empty,-16} {preset.Command} {preset.Arguments}");
            Console.WriteLine($"  {string.Empty,-16} {preset.Note}");
            Console.WriteLine();
        }

        Console.WriteLine("  lanshu lipsync --use wav2lip --dir ~/src/Wav2Lip [--checkpoint <file>]");
        return 0;
    }

    if (line.Has("use"))
    {
        var directory = line.Value("dir")
            ?? throw new ArgumentException("--dir <checkout directory> is required with --use");

        var configured = service.Configure(
            line.Value("use")!,
            directory,
            line.Value("checkpoint"),
            line.Value("python"));

        PrintLipSyncStatus(configured);
        if (configured.Configured)
        {
            Console.WriteLine();
            Console.WriteLine("Verify it with: lanshu lipsync --test");
        }

        return configured.Configured ? 0 : 1;
    }

    if (line.Flag("test"))
    {
        Console.WriteLine("Running a two-second smoke test through the configured tool...");
        var result = await service.TestAsync();
        Console.WriteLine();
        Console.WriteLine(result.Passed ? "PASS  " + result.Detail : "FAIL  " + result.Detail);
        return result.Passed ? 0 : 1;
    }

    PrintLipSyncStatus(service.Describe());
    Console.WriteLine();
    Console.WriteLine("  lanshu lipsync --list              show the known tools");
    Console.WriteLine("  lanshu lipsync --use <preset> --dir <checkout>");
    Console.WriteLine("  lanshu lipsync --test              prove the configured tool runs");
    return 0;
}

void PrintLipSyncStatus(LipSyncStatus status)
{
    Console.WriteLine("Local lip-sync: " + (status.Configured ? "ready" : "not usable yet"));
    Console.WriteLine($"  provider   : {Blank(status.Provider)}");
    Console.WriteLine($"  command    : {Blank(status.Command)}"
                      + (string.IsNullOrWhiteSpace(status.ResolvedCommand) ? string.Empty : $"  -> {status.ResolvedCommand}"));
    Console.WriteLine($"  checkout   : {Blank(status.WorkingDirectory)}");
    Console.WriteLine($"  checkpoint : {Blank(status.CheckpointPath)}");

    foreach (var problem in status.Problems)
    {
        Console.WriteLine("  ! " + problem);
    }

    static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "(not set)" : value;
}

async Task<int> DoctorAsync()
{
    var service = new EnvironmentService(store, httpClient);

    if (line.Flag("install"))
    {
        Console.WriteLine("Installing a portable FFmpeg build into " + ToolLocator.ToolsDirectory);
        var path = await service.InstallFfmpegAsync(Console.WriteLine);
        Console.WriteLine("Installed: " + path);
        store.Reload();
    }

    var report = await service.InspectAsync();
    Console.WriteLine(JobJson.Serialize(report).TrimEnd());

    if (report.Problems.Count > 0)
    {
        Console.Error.WriteLine();
        foreach (var problem in report.Problems)
        {
            Console.Error.WriteLine("  ! " + problem);
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Run 'lanshu doctor --install' to download a portable FFmpeg build.");
    }

    return report.Ok ? 0 : 1;
}

async Task<int> VoicesAsync()
{
    var settings = store.Load();
    var router = new SpeechRouter(settings, store, httpClient);
    var requested = line.Value("provider");

    foreach (var synthesizer in router.All())
    {
        if (requested is not null
            && !synthesizer.Provider.Contains(requested, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!await synthesizer.IsAvailableAsync())
        {
            continue;
        }

        Console.WriteLine($"# {synthesizer.Provider} ({(synthesizer.IsRemote ? "remote" : "local")})");
        foreach (var voice in await synthesizer.ListVoicesAsync())
        {
            Console.WriteLine($"  {voice.Id}\t{voice.DisplayName}\t{voice.Language}");
        }

        Console.WriteLine();
    }

    return 0;
}

int JobsList()
{
    var settings = store.Load();
    var workspace = line.Value("workspace") ?? settings.ResolvedWorkspace;
    var summaries = JobService.Discover(workspace);

    if (summaries.Count == 0)
    {
        Console.WriteLine($"No jobs in {workspace}");
        return 0;
    }

    foreach (var summary in summaries)
    {
        Console.WriteLine($"{summary.State,-20} {summary.JobId,-32} {summary.Directory}");
    }

    return 0;
}

int Config()
{
    var settings = store.Load();

    if (line.Has("set-secret"))
    {
        var key = line.Value("set-secret")!;
        var value = line.Value("value") ?? string.Empty;
        store.SetSecret(key, value);
        Console.WriteLine(string.IsNullOrWhiteSpace(value) ? $"Removed {key}." : $"Stored {key}.");
        return 0;
    }

    if (line.Has("workspace"))
    {
        settings.Workspace = FileSystemUtil.ExpandPath(line.Value("workspace")!);
        store.Save(settings);
        Console.WriteLine("Workspace: " + settings.ResolvedWorkspace);
        return 0;
    }

    if (line.Has("ffmpeg"))
    {
        settings.FfmpegPath = FileSystemUtil.ExpandPath(line.Value("ffmpeg")!);
        store.Save(settings);
        Console.WriteLine("FFmpeg: " + settings.FfmpegPath);
        return 0;
    }

    Console.WriteLine("Settings file: " + store.SettingsFile);
    Console.WriteLine("Secrets file:  " + store.SecretsFile);
    Console.WriteLine("Workspace:     " + settings.ResolvedWorkspace);
    Console.WriteLine();
    Console.WriteLine(JobJson.Serialize(settings).TrimEnd());
    Console.WriteLine();
    Console.WriteLine("Stored credentials: " +
                      (store.SecretNames().Count == 0 ? "none" : string.Join(", ", store.SecretNames())));
    return 0;
}

int Version()
{
    Console.WriteLine($"Lanshu AI Presenter Studio {EnvironmentService.AppVersion}");
    return 0;
}

JobPaths ResolvePaths()
{
    var value = line.Value("job-dir") ?? line.Value("job") ?? line.Positional.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new ArgumentException("--job-dir <directory> is required");
    }

    var expanded = FileSystemUtil.ExpandPath(value);
    if (File.Exists(expanded) && Path.GetFileName(expanded).Equals("job.json", StringComparison.OrdinalIgnoreCase))
    {
        expanded = Path.GetDirectoryName(expanded)!;
    }

    if (!File.Exists(Path.Combine(expanded, "job.json")))
    {
        throw new FileNotFoundException($"no job.json in {expanded}");
    }

    return new JobPaths(expanded);
}

int Help(int exitCode)
{
    Console.WriteLine($"""
        Lanshu AI Presenter Studio {EnvironmentService.AppVersion}
        Turn a script or topic plus one authorized presenter image into a finished video.

        USAGE
          lanshu <command> [options]

        COMMANDS
          init          Create a job directory from a topic or a script and a presenter image
          run           Run the pipeline for a job, resuming from its last completed stage
                        (--preview renders a fast low-resolution proxy instead)
          approve       Record an approval (script, paid generation, pilot, rights, upload, review)
          preflight     Validate a job's inputs and write qa/reports/preflight.json
          finalize      Master, share, verify and contact-sheet an existing render
          segments      List the narration segments of a job
          retake        Re-speak one segment, optionally with a pronunciation respelling
          pilot         Show the pilot's details and lay its frames out as a contact sheet
          lipsync       Configure and smoke-test a locally installed lip-sync tool
          brand         Save, list and delete reusable brand kits
          broll         Choose which supporting media goes on which chapter
          doctor        Report the environment; --install downloads a portable FFmpeg
          voices        List the voices available from every reachable speech engine
          jobs          List jobs in the workspace
          config        Show or change settings and stored credentials
          version       Print the version

        EXAMPLES
          lanshu doctor --install

          lanshu init --topic "Explain context engineering in one minute" \
                      --presenter-image ./presenter.png \
                      --duration 60 --aspect 9:16 --reviewed --rights-confirmed \
                      --adult-presenter-confirmed

          lanshu run --preview --job-dir ~/.lanshu-presenter/jobs/explain-context-engineering-20260825-1200
          lanshu run --job-dir ~/.lanshu-presenter/jobs/explain-context-engineering-20260825-1200

          lanshu finalize --input renders/rendered.mkv --output outputs --stem my-video

        OPTIONS FOR init
          --job-dir <dir>            Where to create the job (defaults to the workspace)
          --topic <text>             Topic to write a script from
          --script <file>            Use an existing script file instead of a topic
          --presenter-image <file>   Required. One authorized image with one clear adult face
          --voice-sample <file>      Optional authorized voice sample
          --supporting-media <file>  Repeatable B-roll, screenshot, or chart
          --duration <seconds>       Target spoken duration (default 60)
          --aspect 9:16|16:9|1:1|4:5|4:3|21:9
          --width/--height <px>      Custom dimensions, supplied together
          --fps 24|25|30|50|60       Frame rate (default 30)
          --language <code>          Defaults to auto-detect
          --style/--audience/--cta/--watermark/--accent/--music
          --no-captions              Skip burned-in captions
          --no-callouts              Skip keyword callouts
          --no-punch-ins             Skip the emphasis push on keyword beats
          --no-multi-shot            Hold one framing instead of cutting wide/medium/close
          --also-aspect <ratio>      Repeatable. Deliver this ratio too, from the same narration
          --no-publishing-kit        Skip thumbnails, chapter markers and the description
          --review-script            Pause after drafting so the narration can be edited
          --reviewed                 Record that you looked at the image yourself
          --rights-confirmed --adult-presenter-confirmed --remote-upload-approved
          --voice-clone-approved
        """);
    return exitCode;
}
