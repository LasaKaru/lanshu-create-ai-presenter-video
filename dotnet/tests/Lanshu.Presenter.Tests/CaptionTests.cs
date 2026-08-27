using Lanshu.Presenter.Core.Asr;
using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Voice;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class CaptionTests
{
    private static NarrationSegment Segment(int index, int beat, string text, double start, double duration) =>
        new(index, beat, text, $"seg-{index}.wav", start, duration);

    private static (ScriptDocument Script, List<NarrationSegment> Segments) Fixture()
    {
        var script = new ScriptDocument
        {
            Title = "One clock",
            Language = "en",
            Beats =
            {
                new ScriptBeat { Role = "hook", Title = "Hook", Keyword = "one clock", Narration = "Most pipelines drift because every stage guesses." },
                new ScriptBeat { Role = "beat", Title = "Beat", Keyword = "measure", Narration = "Lock the narration, then measure it once." },
            },
        };

        var segments = new List<NarrationSegment>
        {
            Segment(0, 0, script.Beats[0].Narration, 0, 4.0),
            Segment(1, 1, script.Beats[1].Narration, 4.4, 3.6),
        };

        return (script, segments);
    }

    [Fact]
    public void AlignerCoversEveryTokenInOrder()
    {
        var (script, segments) = Fixture();
        var aligned = ProportionalAligner.Align(segments);

        var expected = segments.Sum(segment => Lanshu.Presenter.Core.Util.TextUtil.Tokenize(segment.Text).Count);
        Assert.Equal(expected, aligned.Words.Count);
        Assert.False(aligned.WordTimingsAreMeasured);

        for (var index = 1; index < aligned.Words.Count; index++)
        {
            Assert.True(aligned.Words[index].StartSeconds >= aligned.Words[index - 1].StartSeconds - 0.001);
        }

        Assert.True(aligned.Words[0].StartSeconds >= 0);
        Assert.True(aligned.Words[^1].EndSeconds <= segments[^1].EndSeconds + 0.001);
        Assert.NotEmpty(script.Narration);
    }

    [Fact]
    public void PhrasesStayInsideTheFrameBudgetAndNeverOverlap()
    {
        var (script, segments) = Fixture();
        var layout = new CaptionLayout(1080, 1920);
        var plan = new CaptionBuilder().Build(ProportionalAligner.Align(segments), script, segments, true, layout);

        Assert.NotEmpty(plan.Phrases);
        var budget = layout.MaxCharactersPerPhrase(false);
        Assert.All(plan.Phrases, phrase => Assert.True(phrase.Text.Length <= budget, $"'{phrase.Text}' exceeded {budget}"));

        for (var index = 1; index < plan.Phrases.Count; index++)
        {
            Assert.True(
                plan.Phrases[index].StartSeconds >= plan.Phrases[index - 1].EndSeconds - 0.001,
                "captions must not overlap");
        }
    }

    [Fact]
    public void CalloutsBindToBeatWindows()
    {
        var (script, segments) = Fixture();
        var plan = new CaptionBuilder().Build(
            ProportionalAligner.Align(segments), script, segments, true, new CaptionLayout(1080, 1920));

        Assert.Equal(2, plan.Callouts.Count);
        Assert.All(plan.Callouts, callout =>
        {
            Assert.True(callout.EndSeconds > callout.StartSeconds);
            Assert.Contains(callout.Preset, AssWriter.PresetNames);
        });

        // Presets rotate so the same card never repeats back to back.
        Assert.NotEqual(plan.Callouts[0].Preset, plan.Callouts[1].Preset);
    }

    [Fact]
    public void CalloutsAreOmittedWhenTurnedOff()
    {
        var (script, segments) = Fixture();
        var plan = new CaptionBuilder().Build(
            ProportionalAligner.Align(segments), script, segments, false, new CaptionLayout(1080, 1920));
        Assert.Empty(plan.Callouts);
    }

    [Fact]
    public void HighlightSkipsStopWords()
    {
        var script = new ScriptDocument
        {
            Beats = { new ScriptBeat { Role = "hook", Keyword = "the point", Narration = "Here is the point of the whole exercise." } },
        };
        var segments = new List<NarrationSegment> { Segment(0, 0, script.Beats[0].Narration, 0, 3.5) };
        var plan = new CaptionBuilder().Build(
            ProportionalAligner.Align(segments), script, segments, true, new CaptionLayout(1080, 1920));

        Assert.All(plan.Phrases, phrase =>
            Assert.NotEqual("the", phrase.Highlight, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AssOutputIsWellFormedAndEscapesUserText()
    {
        var (script, segments) = Fixture();
        script.Beats[0].Keyword = "brace {test}";
        var plan = new CaptionBuilder().Build(
            ProportionalAligner.Align(segments), script, segments, true, new CaptionLayout(1080, 1920));

        var ass = AssWriter.Build(plan, new CaptionStyleOptions
        {
            Width = 1080,
            Height = 1920,
            AccentColor = "#F4C430",
            Watermark = "@lanshu",
        });

        Assert.Contains("[Script Info]", ass, StringComparison.Ordinal);
        Assert.Contains("PlayResX: 1080", ass, StringComparison.Ordinal);
        Assert.Contains("[V4+ Styles]", ass, StringComparison.Ordinal);
        Assert.Contains("[Events]", ass, StringComparison.Ordinal);
        Assert.Contains("@lanshu", ass, StringComparison.Ordinal);

        // A brace in user text would otherwise open an override block and swallow the line.
        Assert.DoesNotContain("BRACE {TEST}", ass, StringComparison.Ordinal);
        Assert.Contains("BRACE (TEST)", ass, StringComparison.Ordinal);

        foreach (var line in ass.Split('\n').Where(l => l.StartsWith("Dialogue:", StringComparison.Ordinal)))
        {
            Assert.Equal(
                line.Count(character => character == '{'),
                line.Count(character => character == '}'));
        }
    }

    [Fact]
    public void ColoursConvertToTheRightByteOrder()
    {
        // ASS is &HAABBGGRR, so red and blue swap relative to #RRGGBB.
        Assert.Equal("&H0030C4F4", AssWriter.ToAssColor("#F4C430"));
        Assert.Equal("&H00FFFFFF", AssWriter.ToAssColor("#ffffff"));
        Assert.Equal("&H000000FF", AssWriter.ToAssColor("#f00"));
        Assert.Equal("&H0030C4F4", AssWriter.ToAssColor("nonsense"));
    }

    [Fact]
    public void LongKeywordsShrinkToFitTheFrame()
    {
        Assert.Equal(100, AssWriter.FitToWidth("short", 100, 1080, cjk: false));
        Assert.True(AssWriter.FitToWidth("extraordinarily-long-keyword", 100, 1080, cjk: false) < 100);
    }

    [Fact]
    public void SrtTimecodesUseCommaMilliseconds()
    {
        Assert.Equal("00:00:01,500", SrtWriter.Time(1.5));
        Assert.Equal("01:01:01,250", SrtWriter.Time(3661.25));
        Assert.Equal("0:00:01.50", AssWriter.Time(1.5));
    }
}
