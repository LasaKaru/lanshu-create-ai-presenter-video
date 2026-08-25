using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class JobTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanshu-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => FileSystemUtil.TryDeleteDirectory(_root);

    private string ImageFixture()
    {
        // A 1x1 PNG is enough for the path checks; media decoding is covered by preflight.
        var path = Path.Combine(_root, "presenter.png");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        return path;
    }

    [Fact]
    public void CreatesTheFullArtifactTree()
    {
        var paths = new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "job"),
            PresenterImage = ImageFixture(),
            Topic = "One clock",
        });

        Assert.True(File.Exists(paths.ManifestFile));
        foreach (var relative in JobPaths.RelativeDirectories)
        {
            Assert.True(
                Directory.Exists(Path.Combine(paths.Root, relative.Replace('/', Path.DirectorySeparatorChar))),
                $"missing {relative}");
        }
    }

    [Fact]
    public void RefusesToOverwriteANonEmptyDirectory()
    {
        var directory = Path.Combine(_root, "existing");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "important");

        var exception = Assert.Throws<ArgumentException>(() => new JobService().Create(new NewJobRequest
        {
            JobDirectory = directory,
            PresenterImage = ImageFixture(),
            Topic = "One clock",
        }));

        Assert.Contains("absent or empty", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4000)]
    public void RejectsOutOfRangeDurations(double seconds)
    {
        Assert.Throws<ArgumentException>(() => new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "bad-" + seconds),
            PresenterImage = ImageFixture(),
            Topic = "One clock",
            DurationSeconds = seconds,
        }));
    }

    [Fact]
    public void RequiresATopicOrAScript()
    {
        Assert.Throws<ArgumentException>(() => new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "empty"),
            PresenterImage = ImageFixture(),
        }));
    }

    [Fact]
    public void PastedScriptTextBecomesAFileOnDisk()
    {
        var paths = new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "pasted"),
            PresenterImage = ImageFixture(),
            ScriptText = "Lock the narration first.",
        });

        var job = new JobService().Load(paths);
        Assert.True(File.Exists(job.Input.ScriptPath));
        Assert.Contains("Lock the narration", File.ReadAllText(job.Input.ScriptPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AspectPresetsDriveDimensions()
    {
        var paths = new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "wide"),
            PresenterImage = ImageFixture(),
            Topic = "One clock",
            Aspect = "16:9",
        });

        var job = new JobService().Load(paths);
        Assert.Equal(1920, job.Creative.Width);
        Assert.Equal(1080, job.Creative.Height);
    }

    [Fact]
    public void StateOnlyMovesForward()
    {
        var paths = new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "state"),
            PresenterImage = ImageFixture(),
            Topic = "One clock",
        });

        var service = new JobService();
        var job = service.Load(paths);

        service.Advance(paths, job, JobState.Rendered, "rendered");
        service.Advance(paths, job, JobState.AudioLocked, "resumed an earlier stage");

        Assert.Equal(JobState.Rendered, service.Load(paths).StateValue);
    }

    [Fact]
    public void ManifestRoundTripsThroughTheWireFormat()
    {
        var paths = new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "roundtrip"),
            PresenterImage = ImageFixture(),
            Topic = "One clock",
            Watermark = "@lanshu",
        });

        var json = File.ReadAllText(paths.ManifestFile);

        // The wire format must stay compatible with the Python skill's job.json.
        Assert.Contains("\"schema_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"adult_presenter_confirmed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"duration_target_s\"", json, StringComparison.Ordinal);

        var reloaded = new JobService().Load(paths);
        Assert.Equal("@lanshu", reloaded.Creative.Watermark);
        Assert.Equal("intake", reloaded.State);
    }

    [Fact]
    public void RelativePathsKeepReportsPortable()
    {
        var paths = new JobPaths(Path.Combine(_root, "portable"));
        var inside = Path.Combine(paths.Outputs, "video-master.mp4");
        Assert.Equal("outputs/video-master.mp4", paths.Relative(inside));

        // A path outside the job collapses to its filename so no machine path leaks.
        Assert.Equal("elsewhere.mp4", paths.Relative(Path.Combine(Path.GetTempPath(), "elsewhere.mp4")));
    }

    [Fact]
    public void SuppliedScriptsGetChaptersAndKeywords()
    {
        var document = ScriptDocument.FromSuppliedText("""
            # One clock

            ## Hook

            Most pipelines drift because every stage guesses at timing.

            ## Method

            Lock the narration first, then measure the result carefully.
            """, "fallback");

        Assert.Equal("One clock", document.Title);
        Assert.Equal(2, document.Beats.Count);
        Assert.Equal("hook", document.Beats[0].Role);
        Assert.All(document.Beats, beat => Assert.False(string.IsNullOrWhiteSpace(beat.Keyword)));
        Assert.Equal(
            document.Beats.Select(beat => beat.Keyword.ToLowerInvariant()).Distinct().Count(),
            document.Beats.Count);
    }

    [Fact]
    public async Task OutlineWriterProducesSpeakableBeats()
    {
        var document = await new OutlineScriptWriter()
            .WriteAsync(new ScriptRequest { Topic = "context engineering", TargetSeconds = 60 });

        Assert.True(document.Beats.Count >= 4);
        Assert.Equal("hook", document.Beats[0].Role);
        Assert.Equal("close", document.Beats[^1].Role);
        Assert.All(document.Beats, beat => Assert.False(string.IsNullOrWhiteSpace(beat.Narration)));
        Assert.NotEmpty(document.Notes);
    }

    [Fact]
    public void SlugsAndStemsRejectUnsafeCharacters()
    {
        Assert.Equal("one-clock-measured-once", FileSystemUtil.Slugify("One Clock, Measured Once!"));
        Assert.Equal("presenter-video", FileSystemUtil.Slugify("///"));
        Assert.True(FileSystemUtil.IsSafeStem("my-video_1.0"));
        Assert.False(FileSystemUtil.IsSafeStem("../escape"));
        Assert.False(FileSystemUtil.IsSafeStem("bad name"));
    }
}
