using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Delivery;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Presenter;
using Lanshu.Presenter.Core.Timeline;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class PunchInTests
{
    private static KeywordCallout Callout(string text, double start, double end) =>
        new() { Text = text, StartSeconds = start, EndSeconds = end };

    [Fact]
    public void OnePunchPerCalloutWithALeadIn()
    {
        var punches = TimelineBuilder.BuildPunchIns(
            new[] { Callout("one", 4.0, 6.0), Callout("two", 12.0, 14.0) },
            programDuration: 30);

        Assert.Equal(2, punches.Count);

        // The move starts slightly before the word so it is already underway when it lands.
        Assert.True(punches[0].StartSeconds < 4.0);
        Assert.Equal("one", punches[0].Label);
        Assert.All(punches, punch => Assert.True(punch.Scale > 1.0 && punch.Scale < 1.2));
    }

    [Fact]
    public void OverlappingPunchesAreDroppedRatherThanStacked()
    {
        // A frame that never settles reads as a wobble, so the crowded second push is skipped.
        var punches = TimelineBuilder.BuildPunchIns(
            new[] { Callout("one", 4.0, 6.0), Callout("two", 6.1, 8.0) },
            programDuration: 30);

        Assert.Single(punches);
        Assert.Equal("one", punches[0].Label);
    }

    [Fact]
    public void PunchesNeverRunPastTheProgram()
    {
        var punches = TimelineBuilder.BuildPunchIns(
            new[] { Callout("tail", 9.0, 12.0) },
            programDuration: 10);

        Assert.All(punches, punch => Assert.True(punch.EndSeconds <= 10.001));
    }

    [Fact]
    public void ATooShortWindowProducesNoPunch()
    {
        var punches = TimelineBuilder.BuildPunchIns(
            new[] { Callout("blink", 9.9, 10.0) },
            programDuration: 10);

        Assert.Empty(punches);
    }

    [Fact]
    public void WithoutPunchesTheCropExpressionIsAConstant()
    {
        var timeline = new RenderTimeline { DurationSeconds = 20 };
        Assert.Equal("1128", FfmpegCompositor.BuildPunchExpression(timeline, 1128, 1080));
    }

    [Fact]
    public void EachPunchAddsAGuardedEasedTerm()
    {
        var timeline = new RenderTimeline { DurationSeconds = 20 };
        timeline.PunchIns.Add(new PunchIn { Label = "one", StartSeconds = 4, DurationSeconds = 2, Scale = 1.045 });

        var expression = FfmpegCompositor.BuildPunchExpression(timeline, 1128, 1080);

        Assert.Contains("between(t,4,6)", expression, StringComparison.Ordinal);
        Assert.Contains("cos(", expression, StringComparison.Ordinal);
        Assert.Contains("1128", expression, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOversampleThatCannotShrinkStaysConstant()
    {
        var timeline = new RenderTimeline { DurationSeconds = 20 };
        timeline.PunchIns.Add(new PunchIn { StartSeconds = 1, DurationSeconds = 2, Scale = 1.05 });

        // Oversample equal to the target leaves no room to crop into.
        Assert.Equal("1080", FfmpegCompositor.BuildPunchExpression(timeline, 1080, 1080));
    }
}

public class LocalLipSyncTests
{
    private static LocalLipSyncService Service(LocalLipSyncSettings settings) =>
        new(settings, null!, new JobPaths(Path.Combine(Path.GetTempPath(), "lanshu-lip-test")));

    [Fact]
    public void PlaceholdersAreSubstituted()
    {
        var service = Service(new LocalLipSyncSettings
        {
            Arguments = "inference.py --checkpoint_path {{CHECKPOINT}} --face {{VIDEO}} --audio {{AUDIO}} --outfile {{OUTPUT}}",
            CheckpointPath = "/models/wav2lip.pth",
        });

        var arguments = service.BuildArguments("/j/plate.mp4", "/j/narration.wav", "/j/face.png", "/j/out.mp4");

        Assert.Equal(
            new[]
            {
                "inference.py", "--checkpoint_path", "/models/wav2lip.pth",
                "--face", "/j/plate.mp4", "--audio", "/j/narration.wav", "--outfile", "/j/out.mp4",
            },
            arguments);
    }

    [Fact]
    public void OutputDirectoryPlaceholderResolves()
    {
        var service = Service(new LocalLipSyncSettings { Arguments = "run.py --result_dir {{OUTPUT_DIR}} --source {{IMAGE}}" });
        var arguments = service.BuildArguments("v.mp4", "a.wav", "/j/face.png", Path.Combine("/j", "out", "r.mp4"));

        Assert.Contains(Path.GetFullPath("/j/out"), arguments);
        Assert.Contains("/j/face.png", arguments);
    }

    [Fact]
    public void ArgumentsAreSplitBeforeSubstitutionSoPathsStayWhole()
    {
        var service = Service(new LocalLipSyncSettings { Arguments = "run.py --face {{VIDEO}}" });
        var arguments = service.BuildArguments("/a path/with spaces/plate.mp4", "a.wav", "i.png", "o.mp4");

        // The path arrives as one argument even though it contains spaces.
        Assert.Equal(3, arguments.Count);
        Assert.Equal("/a path/with spaces/plate.mp4", arguments[2]);
    }

    [Fact]
    public void NotConfiguredWithoutACommand()
    {
        Assert.False(Service(new LocalLipSyncSettings { Enabled = true, Arguments = "x" }).IsConfigured);
        Assert.False(Service(new LocalLipSyncSettings { Enabled = false, Command = "python", Arguments = "x" }).IsConfigured);
    }

    [Fact]
    public void PresetsCoverTheCommonToolsAndCarryPlaceholders()
    {
        Assert.NotEmpty(LocalLipSyncService.Presets);
        Assert.All(LocalLipSyncService.Presets, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Command));
            Assert.Contains("{{AUDIO}}", preset.Arguments, StringComparison.Ordinal);
        });
    }
}

public class PublishingKitTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(65, "1:05")]
    [InlineData(3661, "1:01:01")]
    [InlineData(-5, "0:00")]
    public void TimecodesMatchTheFormatYouTubeParses(double seconds, string expected)
    {
        Assert.Equal(expected, PublishingKitService.Timecode(seconds));
    }
}
