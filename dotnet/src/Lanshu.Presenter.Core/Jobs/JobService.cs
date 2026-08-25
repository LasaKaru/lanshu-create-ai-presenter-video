using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Jobs;

public sealed record NewJobRequest
{
    public required string JobDirectory { get; init; }

    public required string PresenterImage { get; init; }

    public string? Topic { get; init; }

    public string? ScriptFile { get; init; }

    public string? ScriptText { get; init; }

    public string? VoiceSample { get; init; }

    public IReadOnlyList<string> SupportingMedia { get; init; } = Array.Empty<string>();

    public string Language { get; init; } = "auto";

    public string Audience { get; init; } = "general";

    public double DurationSeconds { get; init; } = 60;

    public string Aspect { get; init; } = "9:16";

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int Fps { get; init; } = 30;

    public string Style { get; init; } = "credible contemporary presenter";

    public string Watermark { get; init; } = string.Empty;

    public string Cta { get; init; } = string.Empty;

    public string AccentColor { get; init; } = "#F4C430";

    public string? MusicPath { get; init; }

    public bool CaptionsEnabled { get; init; } = true;

    public bool KeywordCalloutsEnabled { get; init; } = true;

    public bool PunchInsEnabled { get; init; } = true;

    public IReadOnlyList<string> AdditionalAspects { get; init; } = Array.Empty<string>();

    public bool PublishingKit { get; init; } = true;

    public bool RightsConfirmed { get; init; }

    public bool AdultPresenterConfirmed { get; init; }

    public bool RemoteUploadApproved { get; init; }

    public bool VoiceCloneApproved { get; init; }

    public ManualInputReview? ManualReview { get; init; }
}

public sealed class JobService
{
    public static readonly IReadOnlyDictionary<string, (int Width, int Height)> AspectDefaults =
        new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["9:16"] = (1080, 1920),
            ["16:9"] = (1920, 1080),
            ["1:1"] = (1080, 1080),
            ["4:5"] = (1080, 1350),
            ["4:3"] = (1440, 1080),
            ["21:9"] = (2560, 1080),
        };

    public static readonly int[] AllowedFps = { 24, 25, 30, 50, 60 };

    public JobPaths Create(NewJobRequest request)
    {
        if (request.DurationSeconds is < 5 or > 1800)
        {
            throw new ArgumentException("duration must be between 5 and 1800 seconds");
        }

        if (request.Width.HasValue != request.Height.HasValue)
        {
            throw new ArgumentException("width and height must be supplied together");
        }

        if (!AspectDefaults.TryGetValue(request.Aspect, out var dimensions))
        {
            throw new ArgumentException(
                $"aspect must be one of {string.Join(", ", AspectDefaults.Keys)}");
        }

        if (!AllowedFps.Contains(request.Fps))
        {
            throw new ArgumentException($"fps must be one of {string.Join(", ", AllowedFps)}");
        }

        var width = dimensions.Width;
        var height = dimensions.Height;
        if (request.Width is { } customWidth && request.Height is { } customHeight)
        {
            width = customWidth;
            height = customHeight;
            if (Math.Min(width, height) < 256 || Math.Max(width, height) > 7680)
            {
                throw new ArgumentException("custom dimensions must be between 256 and 7680 pixels");
            }
        }

        var hasTopic = !string.IsNullOrWhiteSpace(request.Topic);
        var hasScriptFile = !string.IsNullOrWhiteSpace(request.ScriptFile);
        var hasScriptText = !string.IsNullOrWhiteSpace(request.ScriptText);
        if (!hasTopic && !hasScriptFile && !hasScriptText)
        {
            throw new ArgumentException("a topic, a script file, or script text is required");
        }

        var jobDirectory = FileSystemUtil.ExpandPath(request.JobDirectory);
        if (Directory.Exists(jobDirectory) && !FileSystemUtil.IsDirectoryEmpty(jobDirectory))
        {
            throw new ArgumentException($"job directory must be absent or empty: {jobDirectory}");
        }

        var paths = new JobPaths(jobDirectory);
        paths.CreateAll();

        var scriptPath = string.Empty;
        if (hasScriptFile)
        {
            scriptPath = FileSystemUtil.ResolveExisting(request.ScriptFile!, "script");
        }
        else if (hasScriptText)
        {
            // A pasted script becomes a real file so every later stage has one source of truth.
            scriptPath = Path.Combine(paths.SourceAssets, "supplied-script.md");
            FileSystemUtil.WriteAllTextUtf8(scriptPath, request.ScriptText!.Trim() + "\n");
        }

        var manifest = new JobManifest
        {
            JobId = FileSystemUtil.Slugify(Path.GetFileName(jobDirectory.TrimEnd(Path.DirectorySeparatorChar))),
            State = JobStates.ToWire(JobState.Intake),
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            Input = new JobInput
            {
                Topic = (request.Topic ?? string.Empty).Trim(),
                ScriptPath = scriptPath,
                PresenterImage = FileSystemUtil.ResolveExisting(request.PresenterImage, "presenter image"),
                VoiceSample = string.IsNullOrWhiteSpace(request.VoiceSample)
                    ? string.Empty
                    : FileSystemUtil.ResolveExisting(request.VoiceSample!, "voice sample"),
                SupportingMedia = request.SupportingMedia
                    .Select(item => FileSystemUtil.ResolveExisting(item, "supporting media"))
                    .ToList(),
                RightsConfirmed = request.RightsConfirmed,
                AdultPresenterConfirmed = request.AdultPresenterConfirmed,
                RemoteUploadApproved = request.RemoteUploadApproved,
                VoiceCloneApproved = request.VoiceCloneApproved,
            },
            Creative = new JobCreative
            {
                Language = request.Language,
                Audience = request.Audience,
                DurationTargetSeconds = request.DurationSeconds,
                Aspect = request.Aspect,
                Width = width,
                Height = height,
                Fps = request.Fps,
                Style = request.Style,
                Watermark = request.Watermark,
                Cta = request.Cta,
                AccentColor = request.AccentColor,
                CaptionsEnabled = request.CaptionsEnabled,
                KeywordCalloutsEnabled = request.KeywordCalloutsEnabled,
                PunchInsEnabled = request.PunchInsEnabled,
                PublishingKit = request.PublishingKit,
                AdditionalAspects = request.AdditionalAspects
                    .Select(aspect => aspect.Trim())
                    .Where(aspect => AspectDefaults.ContainsKey(aspect) && aspect != request.Aspect)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                MusicPath = string.IsNullOrWhiteSpace(request.MusicPath)
                    ? string.Empty
                    : FileSystemUtil.ResolveExisting(request.MusicPath!, "music"),
            },
            ManualInputReview = request.ManualReview ?? new ManualInputReview(),
        };

        manifest.Record("intake", "job created");
        Save(paths, manifest);
        return paths;
    }

    public JobManifest Load(JobPaths paths) => Load(paths.ManifestFile);

    public JobManifest Load(string manifestFile)
    {
        var full = FileSystemUtil.ExpandPath(manifestFile);
        if (Directory.Exists(full))
        {
            full = Path.Combine(full, "job.json");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"job manifest does not exist: {full}", full);
        }

        return JobJson.Deserialize<JobManifest>(File.ReadAllText(full));
    }

    public void Save(JobPaths paths, JobManifest manifest)
    {
        manifest.Touch();
        FileSystemUtil.WriteAtomic(paths.ManifestFile, JobJson.Serialize(manifest));
    }

    /// <summary>Advances state only forward; a resumed job never regresses accepted work.</summary>
    public void Advance(JobPaths paths, JobManifest manifest, JobState state, string message)
    {
        if (state > manifest.StateValue)
        {
            manifest.StateValue = state;
        }

        manifest.Record(JobStates.ToWire(state), message);
        Save(paths, manifest);
    }

    public static IReadOnlyList<JobSummary> Discover(string workspaceRoot)
    {
        var root = FileSystemUtil.ExpandPath(workspaceRoot);
        if (!Directory.Exists(root))
        {
            return Array.Empty<JobSummary>();
        }

        var summaries = new List<JobSummary>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var manifestFile = Path.Combine(directory, "job.json");
            if (!File.Exists(manifestFile))
            {
                continue;
            }

            try
            {
                var manifest = JobJson.Deserialize<JobManifest>(File.ReadAllText(manifestFile));
                summaries.Add(new JobSummary(
                    manifest.JobId,
                    directory,
                    manifest.State,
                    string.IsNullOrWhiteSpace(manifest.Input.Topic)
                        ? Path.GetFileName(manifest.Input.ScriptPath)
                        : manifest.Input.Topic,
                    manifest.UpdatedUtc,
                    manifest.Artifacts.Master,
                    manifest.Artifacts.Share));
            }
            catch (Exception)
            {
                // A corrupt or foreign job.json should not hide the rest of the workspace.
            }
        }

        return summaries
            .OrderByDescending(summary => summary.UpdatedUtc, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed record JobSummary(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("updatedUtc")] string UpdatedUtc,
    [property: JsonPropertyName("master")] string Master,
    [property: JsonPropertyName("share")] string Share);
