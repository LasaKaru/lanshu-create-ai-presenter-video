using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class ScriptEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanshu-edit-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => FileSystemUtil.TryDeleteDirectory(_root);

    private (JobPaths Paths, JobManifest Job) Fixture()
    {
        Directory.CreateDirectory(_root);
        var image = Path.Combine(_root, "presenter.png");
        File.WriteAllBytes(image, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

        var paths = new JobService().Create(new NewJobRequest
        {
            JobDirectory = Path.Combine(_root, "job"),
            PresenterImage = image,
            Topic = "One clock",
        });

        var job = new JobService().Load(paths);

        var script = new ScriptDocument
        {
            Title = "One clock",
            Language = "en",
            Source = "model",
            Beats =
            {
                new ScriptBeat { Role = "hook", Title = "Hook", Narration = "Most pipelines drift.", Keyword = "drift" },
                new ScriptBeat { Role = "close", Title = "Close", Narration = "Lock the narration first.", Keyword = "lock" },
            },
        };

        FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "script.json"), JobJson.Serialize(script));
        return (paths, job);
    }

    [Fact]
    public void ReadsWhatWasWritten()
    {
        var (paths, _) = Fixture();
        var script = new ScriptEditor().Read(paths);

        Assert.NotNull(script);
        Assert.Equal(2, script!.Beats.Count);
        Assert.Equal("hook", script.Beats[0].Role);
    }

    [Fact]
    public void ReadingAJobWithNoDraftReturnsNothing()
    {
        Directory.CreateDirectory(_root);
        var paths = new JobPaths(Path.Combine(_root, "empty"));
        paths.CreateAll();
        Assert.Null(new ScriptEditor().Read(paths));
    }

    [Fact]
    public void SavingRewritesAllThreeScriptArtifacts()
    {
        var (paths, job) = Fixture();

        new ScriptEditor().Save(paths, job, new[]
        {
            new ScriptBeat { Role = "hook", Title = "Hook", Narration = "Rewritten opening line." },
            new ScriptBeat { Role = "close", Title = "Close", Narration = "Rewritten closing line." },
        });

        Assert.Contains("Rewritten opening line.", File.ReadAllText(Path.Combine(paths.Docs, "script.json")), StringComparison.Ordinal);
        Assert.Contains("Rewritten opening line.", File.ReadAllText(Path.Combine(paths.Docs, "SCRIPT.md")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(paths.Docs, "BEAT_SHEET.md")));
    }

    [Fact]
    public void PastedMarkdownIsReducedToSpokenWords()
    {
        var (paths, job) = Fixture();

        var result = new ScriptEditor().Save(paths, job, new[]
        {
            new ScriptBeat { Role = "hook", Narration = "## Heading\n\n- **Lock** the narration. (pause)" },
        });

        var narration = result.Script.Beats[0].Narration;
        Assert.DoesNotContain("**", narration, StringComparison.Ordinal);
        Assert.DoesNotContain("(pause)", narration, StringComparison.Ordinal);
        Assert.Contains("Lock the narration.", narration, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBeatEndsUpWithACalloutKeyword()
    {
        var (paths, job) = Fixture();

        var result = new ScriptEditor().Save(paths, job, new[]
        {
            new ScriptBeat { Role = "hook", Narration = "Deterministic timelines remove the guessing." },
            new ScriptBeat { Role = "close", Narration = "Measured narration keeps everything aligned." },
        });

        Assert.All(result.Script.Beats, beat => Assert.False(string.IsNullOrWhiteSpace(beat.Keyword)));
    }

    [Fact]
    public void EmptyBeatsAreRejected()
    {
        var (paths, job) = Fixture();

        Assert.Throws<ArgumentException>(() => new ScriptEditor().Save(paths, job, new[]
        {
            new ScriptBeat { Role = "hook", Narration = "   " },
        }));
    }

    [Fact]
    public void ChangingTheWordingAfterAudioLockDiscardsTheNarration()
    {
        var (paths, job) = Fixture();

        // Pretend a previous run locked audio and derived captions from it.
        job.StateValue = JobState.Rendered;
        job.Artifacts.FinalAudio = "assets/audio/final/narration.wav";
        job.Artifacts.CaptionAss = "assets/captions/captions.ass";
        job.Voice.Sections.Add(new VoiceSection { Index = 0, Text = "Most pipelines drift." });
        var narrationFile = Path.Combine(paths.AudioFinal, "narration.wav");
        File.WriteAllText(narrationFile, "not really audio");

        var result = new ScriptEditor().Save(paths, job, new[]
        {
            new ScriptBeat { Role = "hook", Narration = "A completely different opening." },
        });

        Assert.True(result.AudioInvalidated);
        Assert.Equal(JobState.ContentLocked, job.StateValue);
        Assert.Empty(job.Voice.Sections);
        Assert.Equal(string.Empty, job.Artifacts.FinalAudio);
        Assert.Equal(string.Empty, job.Artifacts.CaptionAss);
        Assert.False(File.Exists(narrationFile));
    }

    [Fact]
    public void SavingIdenticalWordingKeepsTheLockedAudio()
    {
        var (paths, job) = Fixture();

        job.StateValue = JobState.Rendered;
        job.Artifacts.FinalAudio = "assets/audio/final/narration.wav";
        var narrationFile = Path.Combine(paths.AudioFinal, "narration.wav");
        File.WriteAllText(narrationFile, "not really audio");

        // Only the callout keyword changes; the spoken words are untouched.
        var result = new ScriptEditor().Save(paths, job, new[]
        {
            new ScriptBeat { Role = "hook", Title = "Hook", Narration = "Most pipelines drift.", Keyword = "renamed" },
            new ScriptBeat { Role = "close", Title = "Close", Narration = "Lock the narration first.", Keyword = "lock" },
        });

        Assert.False(result.AudioInvalidated);
        Assert.Equal(JobState.Rendered, job.StateValue);
        Assert.True(File.Exists(narrationFile));
        Assert.Equal("renamed", result.Script.Beats[0].Keyword);
    }

    [Fact]
    public void ApprovingUnblocksTheGate()
    {
        var (paths, job) = Fixture();
        Assert.False(job.Plan.ScriptApproved);

        new ScriptEditor().Save(
            paths, job,
            new[] { new ScriptBeat { Role = "hook", Narration = "Approved wording." } },
            approve: true);

        Assert.True(job.Plan.ScriptApproved);
        Assert.True(new JobService().Load(paths).Plan.ScriptApproved);
    }
}
