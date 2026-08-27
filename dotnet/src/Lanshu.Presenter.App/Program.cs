using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lanshu.Presenter.App;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Pipeline;
using Lanshu.Presenter.Core.Timeline;
using Lanshu.Presenter.Core.Util;
using Lanshu.Presenter.Core.Voice;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

var cliPort = ReadOption(args, "--port");

// Headless is the same API with the browser affordances removed: nothing is opened, the port is
// fixed rather than picked at random, and the token can be supplied so an automation client
// already knows it. Everything a script needs, nothing that assumes a person is watching.
var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
var noBrowser = headless || args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);
var port = int.TryParse(cliPort, out var parsedPort)
    ? parsedPort
    : (headless ? 8760 : FreePort());

// A token keeps other local processes from driving the studio's file and render APIs. In headless
// mode it may be supplied so a caller does not have to scrape it from the console; a supplied one
// is used verbatim, because inventing a different token would silently lock the caller out.
var suppliedToken = ReadOption(args, "--token")
                    ?? System.Environment.GetEnvironmentVariable("HELA_TOKEN")
                    ?? System.Environment.GetEnvironmentVariable("LANSHU_TOKEN");
var token = string.IsNullOrWhiteSpace(suppliedToken)
    ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()
    : suppliedToken.Trim();

// Binding beyond loopback exposes render and file APIs to the network, so it is opt-in, named
// explicitly, and refused without a token the operator chose.
var host = ReadOption(args, "--host") ?? "127.0.0.1";
if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
    && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
    && string.IsNullOrWhiteSpace(suppliedToken))
{
    Console.Error.WriteLine(
        "Refusing to bind to " + host + " with a generated token.");
    Console.Error.WriteLine(
        "Binding off loopback exposes the render and file APIs, so pass --token <value> "
        + "(or set HELA_TOKEN) and keep it secret.");
    return 64;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://{host}:{port}");

builder.Services.AddSingleton(new SettingsStore());
builder.Services.AddSingleton<RunManager>();
builder.Services.AddHttpClient("providers", client => client.Timeout = TimeSpan.FromMinutes(30));
builder.Services.Configure<FormOptions>(options =>
{
    // Presenter images, voice samples and B-roll are uploaded through the browser.
    options.MultipartBodyLengthLimit = 1024L * 1024 * 1024;
});

var app = builder.Build();

var assets = new EmbeddedFileProvider(Assembly.GetExecutingAssembly(), "Lanshu.Presenter.App.wwwroot");

app.Use(async (context, next) =>
{
    // Anything but the shell page needs the session token.
    var path = context.Request.Path.Value ?? "/";
    var supplied = context.Request.Query["t"].ToString();
    if (string.IsNullOrEmpty(supplied))
    {
        supplied = context.Request.Cookies["hela_token"] ?? string.Empty;
    }

    if (string.IsNullOrEmpty(supplied))
    {
        supplied = context.Request.Headers["X-Hela-Token"].ToString();
    }

    var authorized = CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(supplied.PadRight(token.Length)[..token.Length]),
        System.Text.Encoding.UTF8.GetBytes(token));

    if (authorized && context.Request.Query.ContainsKey("t"))
    {
        context.Response.Cookies.Append("hela_token", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
        });
    }

    if (!authorized && path != "/health")
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("This studio session requires the link printed in the console.");
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Text("ok"));

app.MapGet("/", () => ServeAsset(assets, "index.html", "text/html; charset=utf-8"));
app.MapGet("/app.css", () => ServeAsset(assets, "app.css", "text/css; charset=utf-8"));
app.MapGet("/app.js", () => ServeAsset(assets, "app.js", "text/javascript; charset=utf-8"));
app.MapGet("/logo.svg", () => ServeAsset(assets, "logo.svg", "image/svg+xml"));

app.MapGet("/api/environment", async (SettingsStore store, IHttpClientFactory factory, CancellationToken token) =>
{
    var service = new EnvironmentService(store, factory.CreateClient("providers"));
    return Results.Json(await service.InspectAsync(token), JobJson.Options);
});

app.MapPost("/api/environment/install-ffmpeg", async (SettingsStore store, IHttpClientFactory factory, CancellationToken token) =>
{
    try
    {
        var service = new EnvironmentService(store, factory.CreateClient("providers"));
        var path = await service.InstallFfmpegAsync(cancellationToken: token);
        store.Reload();
        return Results.Json(new { ok = true, path });
    }
    catch (Exception exception)
    {
        return Results.Json(new { ok = false, error = exception.Message }, statusCode: 500);
    }
});

app.MapGet("/api/settings", (SettingsStore store) => Results.Json(new
{
    settings = store.Load(),
    settingsFile = store.SettingsFile,
    secretsFile = store.SecretsFile,
    workspace = store.Load().ResolvedWorkspace,
    dataDirectory = ToolLocator.DataDirectory,
    knownSecrets = SettingsStore.KnownSecrets.Select(secret => new
    {
        secret.Key,
        secret.DisplayName,
        secret.Purpose,
        configured = store.HasSecret(secret.Key),
    }),
    aspects = JobService.AspectDefaults.Keys,
    version = EnvironmentService.AppVersion,
}, JobJson.Options));

app.MapPost("/api/settings", async (HttpRequest request, SettingsStore store) =>
{
    var settings = await request.ReadFromJsonAsync<AppSettings>(JobJson.Options);
    if (settings is null)
    {
        return Results.BadRequest(new { error = "no settings supplied" });
    }

    store.Save(settings);
    return Results.Json(new { ok = true });
});

app.MapPost("/api/secrets", async (HttpRequest request, SettingsStore store) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var key = body?["key"]?.GetValue<string>();
    var value = body?["value"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest(new { error = "key is required" });
    }

    store.SetSecret(key, value);
    return Results.Json(new { ok = true, configured = store.HasSecret(key) });
});

app.MapGet("/api/voices", async (SettingsStore store, IHttpClientFactory factory, CancellationToken token) =>
{
    var settings = store.Load();
    var router = new SpeechRouter(settings, store, factory.CreateClient("providers"));
    var groups = new List<object>();

    foreach (var synthesizer in router.All())
    {
        if (!await synthesizer.IsAvailableAsync(token))
        {
            continue;
        }

        IReadOnlyList<VoiceDescriptor> voices;
        try
        {
            voices = await synthesizer.ListVoicesAsync(token);
        }
        catch (Exception)
        {
            voices = Array.Empty<VoiceDescriptor>();
        }

        groups.Add(new
        {
            provider = synthesizer.Provider,
            remote = synthesizer.IsRemote,
            voices = voices.Select(voice => new { voice.Id, voice.DisplayName, voice.Language }),
        });
    }

    return Results.Json(new { groups });
});

app.MapGet("/api/jobs", (SettingsStore store) =>
{
    var workspace = store.Load().ResolvedWorkspace;
    return Results.Json(new { workspace, jobs = JobService.Discover(workspace) }, JobJson.Options);
});

app.MapGet("/api/job", (string dir, SettingsStore store) =>
{
    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(dir));
        var job = new JobService().Load(paths);
        return Results.Json(new
        {
            job,
            directory = paths.Root,
            outputs = Directory.Exists(paths.Outputs)
                ? Directory.EnumerateFiles(paths.Outputs).Select(Path.GetFileName).Order().ToList()
                : new List<string?>(),
        }, JobJson.Options);
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 404);
    }
});

app.MapPost("/api/upload", async (HttpRequest request, SettingsStore store) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "expected a multipart upload" });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "no file supplied" });
    }

    var staging = Path.Combine(store.Load().ResolvedWorkspace, "_uploads", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staging);

    // Keep only the leaf name so a crafted upload cannot escape the staging directory.
    var safeName = Path.GetFileName(file.FileName);
    if (string.IsNullOrWhiteSpace(safeName))
    {
        safeName = "upload";
    }

    var destination = Path.Combine(staging, safeName);
    await using (var stream = File.Create(destination))
    {
        await file.CopyToAsync(stream);
    }

    return Results.Json(new { path = destination, name = safeName, bytes = file.Length });
});

app.MapPost("/api/jobs", async (HttpRequest request, SettingsStore store) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    if (body is null)
    {
        return Results.BadRequest(new { error = "no job supplied" });
    }

    try
    {
        var settings = store.Load();
        var topic = Text(body, "topic");
        var scriptText = Text(body, "scriptText");
        var scriptFile = Text(body, "scriptFile");

        var label = !string.IsNullOrWhiteSpace(topic)
            ? topic
            : !string.IsNullOrWhiteSpace(scriptFile)
                ? Path.GetFileNameWithoutExtension(scriptFile)
                : "presenter-video";

        var directory = Text(body, "jobDirectory");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                settings.ResolvedWorkspace,
                $"{FileSystemUtil.Slugify(label)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
        }

        var newJob = new NewJobRequest
        {
            JobDirectory = directory,
            PresenterImage = Text(body, "presenterImage"),
            Topic = topic,
            ScriptFile = scriptFile,
            ScriptText = scriptText,
            VoiceSample = Text(body, "voiceSample"),
            SupportingMedia = body["supportingMedia"] is JsonArray media
                ? media.Select(item => item?.GetValue<string>() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList()
                : Array.Empty<string>(),
            Language = Text(body, "language", settings.Defaults.Language),
            Audience = Text(body, "audience", "general"),
            DurationSeconds = Number(body, "duration", settings.Defaults.DurationTargetSeconds),
            Aspect = Text(body, "aspect", settings.Defaults.Aspect),
            Fps = (int)Number(body, "fps", settings.Defaults.Fps),
            Style = Text(body, "style", settings.Defaults.Style),
            Watermark = Text(body, "watermark"),
            Cta = Text(body, "cta"),
            AccentColor = Text(body, "accentColor", settings.Defaults.AccentColor),
            MusicPath = Text(body, "music"),
            CaptionsEnabled = Boolean(body, "captions", true),
            KeywordCalloutsEnabled = Boolean(body, "callouts", true),
            PunchInsEnabled = Boolean(body, "punchIns", true),
            PublishingKit = Boolean(body, "publishingKit", true),
            AdditionalAspects = body["additionalAspects"] is JsonArray extra
                ? extra.Select(item => item?.GetValue<string>() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToList()
                : Array.Empty<string>(),
            RightsConfirmed = Boolean(body, "rightsConfirmed", false),
            AdultPresenterConfirmed = Boolean(body, "adultConfirmed", false),
            RemoteUploadApproved = Boolean(body, "remoteUploadApproved", false),
            VoiceCloneApproved = Boolean(body, "voiceCloneApproved", false),
            ManualReview = new ManualInputReview
            {
                ImageViewed = Boolean(body, "imageViewed", false),
                SingleClearFace = Boolean(body, "singleClearFace", false),
                ImageHasNoUnwantedText = Boolean(body, "noUnwantedText", false),
                VoiceSampleListened = Boolean(body, "voiceListened", false),
                SingleClearSpeaker = Boolean(body, "singleSpeaker", false),
            },
        };

        var paths = new JobService().Create(newJob);
        var manifest = new JobService().Load(paths);
        manifest.Plan.ReviewScript = Boolean(body, "reviewScript", false);

        // Voice selection is per job so one identity is used for the whole narration.
        var voiceId = Text(body, "voiceId");
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            manifest.Voice.VoiceId = voiceId;
        }

        var rate = Number(body, "rate", 0);
        if (rate > 0)
        {
            manifest.Voice.Rate = rate;
        }

        new JobService().Save(paths, manifest);
        return Results.Json(new { directory = paths.Root });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapGet("/api/job/script", (string dir) =>
{
    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(dir));
        var job = new JobService().Load(paths);
        var script = new ScriptEditor().Read(paths);
        if (script is null)
        {
            return Results.Json(new { error = "this job has no drafted script yet" }, statusCode: 404);
        }

        return Results.Json(new
        {
            script,
            approved = job.Plan.ScriptApproved,
            state = job.State,
            estimatedSeconds = Math.Round(script.EstimatedSeconds, 1),
            targetSeconds = job.Creative.DurationTargetSeconds,
        }, JobJson.Options);
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapPost("/api/job/script", async (HttpRequest request) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var directory = Text(body, "directory");
    if (string.IsNullOrWhiteSpace(directory))
    {
        return Results.BadRequest(new { error = "directory is required" });
    }

    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(directory));
        var service = new JobService();
        var job = service.Load(paths);

        var beats = new List<ScriptBeat>();
        if (body?["beats"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                beats.Add(new ScriptBeat
                {
                    Role = Text(entry, "role", "beat"),
                    Title = Text(entry, "title"),
                    Narration = Text(entry, "narration"),
                    Keyword = Text(entry, "keyword"),
                    VisualNote = Text(entry, "visual_note"),
                });
            }
        }

        var result = new ScriptEditor().Save(
            paths,
            job,
            beats,
            Text(body, "title"),
            Boolean(body, "approve", false));

        return Results.Json(new
        {
            ok = true,
            audioInvalidated = result.AudioInvalidated,
            estimatedSeconds = Math.Round(result.EstimatedSeconds, 1),
            beats = result.Script.Beats.Count,
        });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapGet("/api/job/segments", (string dir) =>
{
    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(dir));
        var job = new JobService().Load(paths);
        return Results.Json(new
        {
            segments = job.Voice.Sections.OrderBy(section => section.Index).Select(section => new
            {
                section.Index,
                section.Text,
                spokenOverride = section.SpokenOverride,
                startSeconds = section.StartSeconds,
                durationSeconds = section.DurationSeconds,
                audio = section.File,
            }),
            provider = job.Voice.Provider,
            voiceId = job.Voice.VoiceId,
        });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapGet("/api/job/timeline", async (string dir, SettingsStore store, CancellationToken token) =>
{
    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(dir));
        var job = new JobService().Load(paths);

        var timelinePath = Path.Combine(paths.Docs, "timeline.json");
        RenderTimeline? timeline = File.Exists(timelinePath)
            ? JobJson.Deserialize<RenderTimeline>(await File.ReadAllTextAsync(timelinePath, token))
            : null;

        // The waveform is a drawing aid: a job that has not been spoken yet still returns its
        // chapters and assignments so the lanes are usable before the first render.
        var waveform = Waveform.Empty;
        var narration = timeline?.NarrationPath;
        if (!string.IsNullOrWhiteSpace(narration) && File.Exists(narration))
        {
            var settings = store.Load();
            var toolset = await MediaToolset
                .ResolveAsync(settings.FfmpegPath, settings.FfprobePath, cancellationToken: token);
            waveform = await WaveformPeaks.MeasureAsync(new FfmpegService(toolset), narration, cancellationToken: token);
        }

        var assignments = job.Plan.InsertAssignments.ToDictionary(entry => entry.ChapterIndex);

        return Results.Json(new
        {
            durationSeconds = timeline?.DurationSeconds ?? 0,
            width = job.Creative.Width,
            height = job.Creative.Height,
            peaks = waveform.Peaks,
            supportingMedia = job.Input.SupportingMedia
                .Select(media => new { path = media, name = Path.GetFileName(media) }),
            chapters = job.Plan.Chapters.OrderBy(chapter => chapter.Index).Select(chapter => new
            {
                index = chapter.Index,
                title = chapter.Title,
                role = chapter.Role,
                startSeconds = chapter.StartSeconds,
                durationSeconds = chapter.DurationSeconds,
                assigned = assignments.TryGetValue(chapter.Index, out var entry) ? entry.Media : null,
                assignedOffset = assignments.TryGetValue(chapter.Index, out var offset) ? offset.OffsetSeconds : -1,
                assignedDuration = assignments.TryGetValue(chapter.Index, out var hold) ? hold.DurationSeconds : 0,
                isAssigned = assignments.ContainsKey(chapter.Index),
            }),
            clips = (timeline?.Clips ?? new List<TimelineClip>()).Select(clip => new
            {
                kind = clip.Kind,
                label = clip.Label,
                name = Path.GetFileName(clip.Source),
                startSeconds = clip.AuthoredStartSeconds,
                durationSeconds = clip.AuthoredDurationSeconds,
            }),
            punchIns = (timeline?.PunchIns ?? new List<PunchIn>()).Select(punch => new
            {
                label = punch.Label,
                startSeconds = punch.StartSeconds,
                durationSeconds = punch.DurationSeconds,
            }),
            shots = (timeline?.Shots ?? new List<Shot>()).Select(shot => new
            {
                label = shot.Label,
                startSeconds = shot.StartSeconds,
                durationSeconds = shot.DurationSeconds,
                scale = shot.Scale,
            }),
        });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapPost("/api/job/timeline", async (HttpRequest request) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var directory = Text(body, "directory");
    if (string.IsNullOrWhiteSpace(directory))
    {
        return Results.BadRequest(new { error = "directory is required" });
    }

    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(directory));
        var jobs = new JobService();
        var job = jobs.Load(paths);

        // The editor always sends the whole set, so a removed row means removed rather than
        // unchanged: a partial merge would make it impossible to un-assign a chapter.
        var replacement = new List<InsertAssignment>();
        if (body?["assignments"] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonObject>())
            {
                var chapterIndex = (int)Number(row, "chapterIndex", -1);
                if (chapterIndex < 0)
                {
                    continue;
                }

                replacement.Add(new InsertAssignment
                {
                    ChapterIndex = chapterIndex,
                    Media = Text(row, "media") ?? string.Empty,
                    OffsetSeconds = Number(row, "offsetSeconds", -1),
                    DurationSeconds = Number(row, "durationSeconds", 0),
                });
            }
        }

        job.Plan.InsertAssignments = replacement;
        jobs.Save(paths, job);

        return Results.Json(new { ok = true, count = replacement.Count });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapPost("/api/job/retake", async (HttpRequest request) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var directory = Text(body, "directory");
    if (string.IsNullOrWhiteSpace(directory))
    {
        return Results.BadRequest(new { error = "directory is required" });
    }

    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(directory));
        var job = new JobService().Load(paths);
        var index = (int)Number(body, "index", -1);

        // An absent "say" leaves any existing override alone; an empty one clears it.
        var spoken = body?["say"] is null ? null : Text(body, "say");

        var result = new SegmentRetakeService().Request(paths, job, index, spoken);
        return Results.Json(new
        {
            ok = true,
            index = result.Index,
            text = result.Text,
            spokenText = result.SpokenText,
            overrideChanged = result.OverrideChanged,
        });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapPost("/api/jobs/run", async (HttpRequest request, RunManager runs) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var directory = Text(body, "directory");
    if (string.IsNullOrWhiteSpace(directory))
    {
        return Results.BadRequest(new { error = "directory is required" });
    }

    try
    {
        var paths = new JobPaths(FileSystemUtil.ExpandPath(directory));
        if (!File.Exists(paths.ManifestFile))
        {
            return Results.Json(new { error = "no job.json in that directory" }, statusCode: 404);
        }

        runs.Prune();
        var record = runs.Start(paths, new PipelineOptions
        {
            AudioOnly = Boolean(body, "audioOnly", false),
            Preview = Boolean(body, "preview", false),
            PreviewHeight = (int)Number(body, "previewHeight", 0),
            Force = Boolean(body, "force", false),
            Overwrite = true,
        });

        return Results.Json(new { runId = record.Id });
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapPost("/api/jobs/cancel", async (HttpRequest request, RunManager runs) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var runId = Text(body, "runId");
    if (!string.IsNullOrWhiteSpace(runId))
    {
        runs.Cancel(runId);
    }

    return Results.Json(new { ok = true });
});

app.MapPost("/api/jobs/approve", async (HttpRequest request) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var directory = Text(body, "directory");
    var kind = Text(body, "kind");
    if (string.IsNullOrWhiteSpace(directory))
    {
        return Results.BadRequest(new { error = "directory is required" });
    }

    var paths = new JobPaths(FileSystemUtil.ExpandPath(directory));
    var service = new JobService();
    var job = service.Load(paths);

    switch (kind)
    {
        case "paid_generation":
            job.Plan.PaidGenerationApproved = true;
            job.Record("plan", "paid generation approved in the studio");
            break;

        case "script":
            job.Plan.ScriptApproved = true;
            job.Record("plan", "script approved in the studio");
            break;

        case "pilot":
            job.Plan.PilotApproved = true;
            job.Record("plan", "pilot approved in the studio");
            break;

        case "rights":
            job.Input.RightsConfirmed = true;
            job.Input.AdultPresenterConfirmed = true;
            break;

        case "remote_upload":
            job.Input.RemoteUploadApproved = true;
            break;

        default:
            return Results.BadRequest(new { error = $"unknown approval '{kind}'" });
    }

    service.Save(paths, job);
    return Results.Json(new { ok = true });
});

app.MapGet("/api/runs/{id}/events", async (string id, RunManager runs, HttpContext context) =>
{
    var record = runs.Find(id);
    if (record is null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    // A reconnecting page resumes from the event index it already has.
    var from = int.TryParse(context.Request.Query["from"], out var parsed) ? Math.Max(0, parsed) : 0;

    try
    {
        await foreach (var item in record.ReadFromAsync(from, context.RequestAborted))
        {
            await WriteEventAsync(context, item);
        }
    }
    catch (OperationCanceledException)
    {
        // The page navigated away; nothing to clean up.
    }
});

app.MapGet("/api/file", (string path, SettingsStore store, HttpContext context) =>
{
    try
    {
        var full = FileSystemUtil.ExpandPath(path);
        if (!IsInsideAllowedRoot(full, store))
        {
            return Results.Json(new { error = "that path is outside the workspace" }, statusCode: 403);
        }

        if (!File.Exists(full))
        {
            return Results.Json(new { error = "not found" }, statusCode: 404);
        }

        var download = context.Request.Query.ContainsKey("download");
        return Results.File(full, ContentType(full), download ? Path.GetFileName(full) : null, enableRangeProcessing: true);
    }
    catch (Exception exception)
    {
        return Results.Json(new { error = exception.Message }, statusCode: 400);
    }
});

app.MapGet("/api/text", async (string path, SettingsStore store) =>
{
    var full = FileSystemUtil.ExpandPath(path);
    if (!IsInsideAllowedRoot(full, store) || !File.Exists(full))
    {
        return Results.Json(new { error = "not available" }, statusCode: 404);
    }

    var text = await File.ReadAllTextAsync(full);
    return Results.Text(text.Length > 400_000 ? text[..400_000] : text, "text/plain; charset=utf-8");
});

app.MapPost("/api/reveal", async (HttpRequest request, SettingsStore store) =>
{
    var body = await request.ReadFromJsonAsync<JsonObject>();
    var path = Text(body, "path");
    var full = FileSystemUtil.ExpandPath(path);
    if (!IsInsideAllowedRoot(full, store))
    {
        return Results.Json(new { error = "that path is outside the workspace" }, statusCode: 403);
    }

    var target = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
    if (!Directory.Exists(target))
    {
        return Results.Json(new { error = "not found" }, statusCode: 404);
    }

    TryOpen(target);
    return Results.Json(new { ok = true });
});

var url = $"http://{host}:{port}/?t={token}";

if (headless)
{
    // One machine-readable line, so a supervisor can capture the address and token without
    // parsing a banner meant for a person.
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        service = "helapresenter",
        version = EnvironmentService.AppVersion,
        host,
        port,
        token,
        url,
        api = new[]
        {
            "GET  /api/jobs",
            "POST /api/jobs",
            "POST /api/jobs/run",
            "GET  /api/job?dir=",
            "GET  /api/job/timeline?dir=",
            "POST /api/jobs/approve",
            "GET  /api/runs/{id}/events",
        },
    }));
}
else
{
    Console.WriteLine();
    Console.WriteLine("  HelaPresenter " + EnvironmentService.AppVersion);
    Console.WriteLine("  " + url);
    Console.WriteLine();
    Console.WriteLine("  Keep this window open while the studio is running. Press Ctrl+C to stop.");
    Console.WriteLine("  done by HelaO2 PVT LTD");
    Console.WriteLine();
}

if (!noBrowser)
{
    TryOpen(url);
}

app.Run();
return 0;

static IResult ServeAsset(IFileProvider provider, string name, string contentType)
{
    var file = provider.GetFileInfo(name);
    if (!file.Exists)
    {
        return Results.NotFound();
    }

    return Results.Stream(file.CreateReadStream(), contentType);
}

static async Task WriteEventAsync(HttpContext context, object payload)
{
    await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload) + "\n\n");
    await context.Response.Body.FlushAsync();
}

static bool IsInsideAllowedRoot(string path, SettingsStore store)
{
    var roots = new[]
    {
        store.Load().ResolvedWorkspace,
        ToolLocator.DataDirectory,
    };

    var full = Path.GetFullPath(path);
    return roots.Any(root =>
    {
        var normalized = Path.GetFullPath(root);
        if (!normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            normalized += Path.DirectorySeparatorChar;
        }

        return full.StartsWith(normalized, ToolLocator.IsWindows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    });
}

static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".mp4" => "video/mp4",
    ".mkv" => "video/x-matroska",
    ".webm" => "video/webm",
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".webp" => "image/webp",
    ".gif" => "image/gif",
    ".wav" => "audio/wav",
    ".mp3" => "audio/mpeg",
    ".json" => "application/json",
    ".srt" or ".ass" or ".md" or ".txt" => "text/plain; charset=utf-8",
    _ => "application/octet-stream",
};

static void TryOpen(string target)
{
    try
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }
    catch (Exception)
    {
        if (ToolLocator.IsWindows)
        {
            return;
        }

        // UseShellExecute is not wired up on every Linux desktop; fall back to xdg-open.
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            // Headless: the console already printed the URL.
        }
    }
}

static string? ReadOption(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}

static int FreePort()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var assigned = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return assigned;
}

static string Text(JsonNode? node, string key, string fallback = "")
{
    var value = node?[key];
    if (value is null)
    {
        return fallback;
    }

    try
    {
        var text = value.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
    }
    catch (Exception)
    {
        return fallback;
    }
}

static double Number(JsonNode? node, string key, double fallback)
{
    try
    {
        return node?[key]?.GetValue<double>() ?? fallback;
    }
    catch (Exception)
    {
        return double.TryParse(Text(node, key), out var parsed) ? parsed : fallback;
    }
}

static bool Boolean(JsonNode? node, string key, bool fallback)
{
    try
    {
        return node?[key]?.GetValue<bool>() ?? fallback;
    }
    catch (Exception)
    {
        return fallback;
    }
}
