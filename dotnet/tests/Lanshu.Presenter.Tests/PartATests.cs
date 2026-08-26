using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;
using Lanshu.Presenter.Core.Voice;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class VoiceCloneEligibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanshu-clone-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => FileSystemUtil.TryDeleteDirectory(_root);

    private string Sample()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "sample.wav");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    [Fact]
    public void AllowedOnlyWhenBothApprovalsAreRecorded()
    {
        var eligibility = CloneEligibility.Evaluate(Sample(), true, true, false);
        Assert.True(eligibility.Allowed);
    }

    [Fact]
    public void RefusedWithoutTheSampleOwnersPermission()
    {
        // The whole point of the gate: a voice is a person's likeness.
        var eligibility = CloneEligibility.Evaluate(Sample(), voiceCloneApproved: false, remoteUploadApproved: true, false);
        Assert.False(eligibility.Allowed);
        Assert.Contains("voice_clone_approved", eligibility.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedWithoutUploadApproval()
    {
        var eligibility = CloneEligibility.Evaluate(Sample(), voiceCloneApproved: true, remoteUploadApproved: false, false);
        Assert.False(eligibility.Allowed);
        Assert.Contains("remote_upload_approved", eligibility.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusedWithoutASample()
    {
        Assert.False(CloneEligibility.Evaluate(string.Empty, true, true, false).Allowed);
        Assert.False(CloneEligibility.Evaluate("/nowhere/missing.wav", true, true, false).Allowed);
    }

    [Fact]
    public void RefusedWhenAVoiceIsAlreadyChosen()
    {
        var eligibility = CloneEligibility.Evaluate(Sample(), true, true, alreadyHaveVoiceId: true);
        Assert.False(eligibility.Allowed);
        Assert.Contains("already has a voice id", eligibility.Reason, StringComparison.Ordinal);
    }
}

public class SegmentRetakeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lanshu-retake-" + Guid.NewGuid().ToString("N"));

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
        job.StateValue = JobState.Verified;
        job.Artifacts.FinalAudio = "assets/audio/final/narration.wav";
        job.Artifacts.CaptionAss = "assets/captions/captions.ass";

        for (var index = 0; index < 3; index++)
        {
            var relative = $"assets/audio/reference/seg-{index + 1:00}.wav";
            File.WriteAllText(paths.Resolve(relative), "audio");
            job.Voice.Sections.Add(new VoiceSection
            {
                Index = index,
                Text = $"Sentence {index}.",
                File = relative,
                DurationSeconds = 2,
                StartSeconds = index * 2.5,
            });
        }

        File.WriteAllText(Path.Combine(paths.AudioFinal, "narration.wav"), "assembled");
        new JobService().Save(paths, job);
        return (paths, job);
    }

    [Fact]
    public void OnlyTheRetakenSegmentLosesItsAudio()
    {
        var (paths, job) = Fixture();

        new SegmentRetakeService().Request(paths, job, 1);

        // The saving is the whole point: on a paid engine only this segment is charged again.
        Assert.False(File.Exists(paths.Resolve("assets/audio/reference/seg-02.wav")));
        Assert.True(File.Exists(paths.Resolve("assets/audio/reference/seg-01.wav")));
        Assert.True(File.Exists(paths.Resolve("assets/audio/reference/seg-03.wav")));
    }

    [Fact]
    public void DerivedArtifactsAreInvalidatedBecauseTimingsShift()
    {
        var (paths, job) = Fixture();

        new SegmentRetakeService().Request(paths, job, 0);

        Assert.Equal(JobState.ContentLocked, job.StateValue);
        Assert.Equal(string.Empty, job.Artifacts.FinalAudio);
        Assert.Equal(string.Empty, job.Artifacts.CaptionAss);
        Assert.False(File.Exists(Path.Combine(paths.AudioFinal, "narration.wav")));
    }

    [Fact]
    public void AnOverrideChangesWhatIsSpokenButNotTheScript()
    {
        var (paths, job) = Fixture();

        var result = new SegmentRetakeService().Request(paths, job, 2, "Sentence two, respelled.");

        Assert.Equal("Sentence 2.", result.Text);
        Assert.Equal("Sentence two, respelled.", result.SpokenText);
        Assert.True(result.OverrideChanged);

        var section = job.Voice.Sections.Single(candidate => candidate.Index == 2);
        Assert.Equal("Sentence 2.", section.Text);
        Assert.Equal("Sentence two, respelled.", section.SpokenOverride);
    }

    [Fact]
    public void AnOverrideEqualToTheScriptIsNotStored()
    {
        var (paths, job) = Fixture();

        new SegmentRetakeService().Request(paths, job, 0, "Sentence 0.");

        Assert.Equal(string.Empty, job.Voice.Sections[0].SpokenOverride);
    }

    [Fact]
    public void ClearingAnOverrideRestoresTheScriptWording()
    {
        var (paths, job) = Fixture();
        var service = new SegmentRetakeService();

        service.Request(paths, job, 0, "Respelled.");
        Assert.Equal("Respelled.", job.Voice.Sections[0].SpokenOverride);

        var cleared = service.ClearOverride(paths, job, 0);
        Assert.Equal(string.Empty, job.Voice.Sections[0].SpokenOverride);
        Assert.Equal(cleared.Text, cleared.SpokenText);
    }

    [Fact]
    public void OmittingTheOverrideLeavesAnExistingOneAlone()
    {
        var (paths, job) = Fixture();
        var service = new SegmentRetakeService();

        service.Request(paths, job, 0, "Respelled.");
        var again = service.Request(paths, job, 0);

        Assert.Equal("Respelled.", job.Voice.Sections[0].SpokenOverride);
        Assert.False(again.OverrideChanged);
    }

    [Fact]
    public void RetakeIsFlaggedSoTheNextRunDoesNotReuseTheSegment()
    {
        var (paths, job) = Fixture();
        new SegmentRetakeService().Request(paths, job, 1);
        Assert.True(job.Voice.Sections.Single(section => section.Index == 1).RetakeRequested);
    }

    [Fact]
    public void AnUnknownSegmentIsRejected()
    {
        var (paths, job) = Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => new SegmentRetakeService().Request(paths, job, 99));
    }

    [Fact]
    public void EffectiveSpokenTextFallsBackToTheScript()
    {
        var section = new VoiceSection { Text = "Script wording." };
        Assert.Equal("Script wording.", section.EffectiveSpokenText);

        section.SpokenOverride = "Respelled.";
        Assert.Equal("Respelled.", section.EffectiveSpokenText);
    }
}
