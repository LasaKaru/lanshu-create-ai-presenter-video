using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Presenter;
using Lanshu.Presenter.Core.Timeline;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class AudioEnvelopeTests
{
    private static byte[] Tone(int sampleRate, double seconds, double amplitude)
    {
        var count = (int)(sampleRate * seconds);
        var bytes = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            var value = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 220 * index / sampleRate));
            BitConverter.GetBytes(value).CopyTo(bytes, index * 2);
        }

        return bytes;
    }

    [Fact]
    public void SilenceProducesAFlatCurve()
    {
        var envelope = AudioEnvelope.FromPcm(new byte[16000], 8000, 25, 25);
        Assert.Equal(25, envelope.FrameCount);
        Assert.All(Enumerable.Range(0, 25), frame => Assert.Equal(0, envelope.At(frame), 4));
    }

    [Fact]
    public void ReadingOutsideTheCurveClampsInsteadOfThrowing()
    {
        var envelope = AudioEnvelope.FromPcm(Tone(8000, 1, 0.5), 8000, 25, 25);
        Assert.Equal(envelope.At(0), envelope.At(-10));
        Assert.Equal(envelope.At(24), envelope.At(9999));
    }

    [Fact]
    public void LoudAudioRisesTowardsOneAndStaysInRange()
    {
        var envelope = AudioEnvelope.FromPcm(Tone(8000, 2, 0.9), 8000, 25, 50);

        Assert.All(Enumerable.Range(0, 50), frame =>
        {
            var value = envelope.At(frame);
            Assert.InRange(value, 0, 1);
        });

        // The attack is fast, so a sustained tone should be well up within half a second.
        Assert.True(envelope.At(20) > 0.5, $"envelope only reached {envelope.At(20):0.00}");
    }

    [Fact]
    public void TheCurveReleasesMoreSlowlyThanItAttacks()
    {
        // Loud for a second, then silence: the fall should lag the rise.
        var loud = Tone(8000, 1, 0.9);
        var quiet = new byte[16000];
        var combined = loud.Concat(quiet).ToArray();

        var envelope = AudioEnvelope.FromPcm(combined, 8000, 25, 50);

        var atOnset = envelope.At(6);
        var justAfterCut = envelope.At(28);
        Assert.True(atOnset > 0.3, "should rise quickly");
        Assert.True(justAfterCut > 0.05, "should not drop instantly");
    }

    [Fact]
    public void ShortPcmDoesNotOverrunTheRequestedFrames()
    {
        var envelope = AudioEnvelope.FromPcm(Tone(8000, 0.2, 0.8), 8000, 25, 100);
        Assert.Equal(100, envelope.FrameCount);
    }
}

public class MotionScriptTests
{
    private static MotionScript.Options Options(int frames) => new()
    {
        SourceWidth = 1200,
        SourceHeight = 2100,
        TargetWidth = 1080,
        TargetHeight = 1920,
        Fps = 30,
        FrameCount = frames,
        SwayPixels = 10,
    };

    [Fact]
    public void EveryCommandKeepsTheWindowInsideThePlate()
    {
        var envelope = AudioEnvelope.FromPcm(new byte[64000], 8000, 30, 90);
        var script = MotionScript.Build(Options(90), envelope);

        foreach (var line in script.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var x = int.Parse(line.Split("crop x ")[1].Split(',')[0]);
            var y = int.Parse(line.Split("crop y ")[1].TrimEnd(';'));

            Assert.InRange(x, 0, 1200 - 1080);
            Assert.InRange(y, 0, 2100 - 1920);
        }
    }

    [Fact]
    public void CommandsAreOrderedInTimeAndUseTheSendcmdSyntax()
    {
        var envelope = AudioEnvelope.FromPcm(new byte[64000], 8000, 30, 60);
        var script = MotionScript.Build(Options(60), envelope);
        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.NotEmpty(lines);
        var previous = -1.0;
        foreach (var line in lines)
        {
            Assert.EndsWith(";", line, StringComparison.Ordinal);
            Assert.Contains("crop x", line, StringComparison.Ordinal);
            Assert.Contains("crop y", line, StringComparison.Ordinal);

            var time = double.Parse(line.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(time > previous, "commands must advance in time");
            previous = time;
        }
    }

    [Fact]
    public void ATargetTheSameSizeAsThePlateHasNowhereToPan()
    {
        var options = Options(30) with { SourceWidth = 1080, SourceHeight = 1920 };
        var script = MotionScript.Build(options, AudioEnvelope.Silent(30, 30));

        foreach (var line in script.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.Contains("crop x 0", line, StringComparison.Ordinal);
            Assert.Contains("crop y 0", line, StringComparison.Ordinal);
        }
    }
}

public class ShotFramingTests
{
    private static PlanChapter Chapter(int index, double start, double duration) => new()
    {
        Index = index,
        Title = $"Chapter {index}",
        Role = index == 0 ? "hook" : "beat",
        StartSeconds = start,
        DurationSeconds = duration,
    };

    [Fact]
    public void TheOpeningAndTheCloseStayWide()
    {
        var shots = TimelineBuilder.BuildShots(
            new[] { Chapter(0, 0, 6), Chapter(1, 6, 6), Chapter(2, 12, 6), Chapter(3, 18, 6) },
            programDuration: 24);

        Assert.Equal(4, shots.Count);
        Assert.Equal(1.0, shots[0].Scale);
        Assert.Equal(1.0, shots[^1].Scale);
    }

    [Fact]
    public void NeighbouringBodyChaptersNeverShareAFraming()
    {
        var shots = TimelineBuilder.BuildShots(
            new[] { Chapter(0, 0, 6), Chapter(1, 6, 6), Chapter(2, 12, 6), Chapter(3, 18, 6), Chapter(4, 24, 6) },
            programDuration: 30);

        // A cut between two identical framings reads as a glitch rather than an edit.
        for (var index = 1; index < shots.Count; index++)
        {
            Assert.NotEqual(shots[index - 1].Scale, shots[index].Scale);
        }
    }

    [Fact]
    public void AChapterTooShortToRegisterStaysWide()
    {
        var shots = TimelineBuilder.BuildShots(
            new[] { Chapter(0, 0, 6), Chapter(1, 6, 1.2), Chapter(2, 7.2, 6) },
            programDuration: 14);

        Assert.Equal(1.0, shots[1].Scale);
    }

    [Fact]
    public void ATighterFramingSitsHigherSoItLandsOnTheFace()
    {
        var shots = TimelineBuilder.BuildShots(
            new[] { Chapter(0, 0, 6), Chapter(1, 6, 6), Chapter(2, 12, 6) },
            programDuration: 18);

        var tight = shots.Single(shot => shot.Scale > 1.0);
        Assert.True(tight.YBias < 0, "a close framing must bias upward");
    }

    [Fact]
    public void TheLastShotIsClampedToTheProgram()
    {
        var shots = TimelineBuilder.BuildShots(
            new[] { Chapter(0, 0, 6), Chapter(1, 6, 6), Chapter(2, 12, 20) },
            programDuration: 20);

        Assert.True(shots[^1].EndSeconds <= 20.001);
    }

    [Fact]
    public void FramingAndEmphasisComposeInTheCropExpression()
    {
        var timeline = new RenderTimeline { DurationSeconds = 20 };
        timeline.Shots.Add(new Shot { StartSeconds = 0, DurationSeconds = 10, Scale = 1.2 });
        timeline.PunchIns.Add(new PunchIn { StartSeconds = 2, DurationSeconds = 2, Scale = 1.05 });

        var expression = FfmpegCompositor.BuildPunchExpression(timeline, 1360, 1080);

        // The punch must ease from the shot's resting size, not from the untouched oversample.
        Assert.Contains("between(t,0,10)", expression, StringComparison.Ordinal);
        Assert.Contains("between(t,2,4)", expression, StringComparison.Ordinal);
        Assert.DoesNotContain("1360-", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutBiasedShotsTheWindowStaysCentred()
    {
        var timeline = new RenderTimeline();
        Assert.Equal("(ih-oh)/2", FfmpegCompositor.BuildVerticalExpression(timeline));
    }
}

public class BackgroundSettingsTests
{
    [Fact]
    public void ReplacementIsOffUnlessAModeIsChosen()
    {
        Assert.False(new Lanshu.Presenter.Core.Configuration.BackgroundSettings().IsEnabled);
        Assert.True(new Lanshu.Presenter.Core.Configuration.BackgroundSettings { Mode = "chroma" }.IsEnabled);
        Assert.False(new Lanshu.Presenter.Core.Configuration.BackgroundSettings { Mode = "NONE" }.IsEnabled);
    }
}
