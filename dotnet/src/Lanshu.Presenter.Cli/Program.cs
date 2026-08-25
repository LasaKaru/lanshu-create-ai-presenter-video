using System.Globalization;
using Lanshu.Presenter.Cli;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Pipeline;
using Lanshu.Presenter.Core.Preflight;
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

    if (line.Flag("review-script"))
    {
        var jobs = new JobService();
        var manifest = jobs.Load(paths);
        manifest.Plan.ReviewScript = true;
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
          --also-aspect <ratio>      Repeatable. Deliver this ratio too, from the same narration
          --no-publishing-kit        Skip thumbnails, chapter markers and the description
          --review-script            Pause after drafting so the narration can be edited
          --reviewed                 Record that you looked at the image yourself
          --rights-confirmed --adult-presenter-confirmed --remote-upload-approved
          --voice-clone-approved
        """);
    return exitCode;
}
