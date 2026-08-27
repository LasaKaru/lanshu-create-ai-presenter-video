using Lanshu.Presenter.Core.Asr;
using Lanshu.Presenter.Core.Branding;
using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Delivery;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class FillerTrimmerTests
{
    [Fact]
    public void DropsFillersAndTidiesThePunctuationTheyLeaveBehind()
    {
        var result = FillerTrimmer.Trim("Um, most dashboards report activity. You know, that gap costs money.");

        Assert.Equal(2, result.Removed);
        Assert.Equal("Most dashboards report activity. That gap costs money.", result.Text);
    }

    [Fact]
    public void RecapitalizesWhenTheFillerWasTheOpeningWord()
    {
        var result = FillerTrimmer.Trim("Uh, the baseline matters.");

        Assert.Equal("The baseline matters.", result.Text);
    }

    [Theory]
    [InlineData("The album is a hummable record.")]
    [InlineData("Ermine is a kind of fur.")]
    [InlineData("She said ahem and carried on.")]
    public void LeavesRealWordsThatMerelyContainAFillerAlone(string text)
    {
        var result = FillerTrimmer.Trim(text);

        Assert.False(result.Changed);
        Assert.Equal(text, result.Text);
    }

    [Fact]
    public void LeavesHedgesAloneBecauseTheyCarryEmphasis()
    {
        // "just" and "really" change what a sentence means; silently deleting them is a rewrite.
        var result = FillerTrimmer.Trim("This is just really important.");

        Assert.False(result.Changed);
    }

    [Fact]
    public void CutsADiscourseMarkerOnlyWhenPunctuationSetsItOff()
    {
        // Set off by a comma, it is padding.
        Assert.Equal("That gap costs money.", FillerTrimmer.Trim("You know, that gap costs money.").Text);

        // Load-bearing in the sentence, it stays.
        var kept = FillerTrimmer.Trim("Do you know the answer?");
        Assert.False(kept.Changed);
    }

    [Fact]
    public void AMidSentenceInterruptionTakesBothItsCommasWithIt()
    {
        var result = FillerTrimmer.Trim("The baseline, you know, is the whole point.");

        Assert.Equal("The baseline is the whole point.", result.Text);
    }

    [Fact]
    public void ATrailingInterruptionLeavesTheSentenceTerminatorAlone()
    {
        var result = FillerTrimmer.Trim("That gap costs money, you know.");

        Assert.Equal("That gap costs money.", result.Text);
    }

    [Fact]
    public void ReportsNoChangeForCleanCopy()
    {
        var result = FillerTrimmer.Trim("A baseline, a control group, and a decision rule.");

        Assert.False(result.Changed);
        Assert.Equal(0, result.Removed);
    }
}

public class SilenceWindowTests
{
    [Fact]
    public void FindsSpeechAfterLeadingPadding()
    {
        // Silence from the first sample to 0.8s, then speech to the end.
        var log = "silence_start: 0\nsilence_end: 0.8 | silence_duration: 0.8\n";

        var (start, end) = SilenceTrimmer.FindSpeechWindow(log, 2.9);

        Assert.InRange(start, 0.75, 0.8);
        Assert.Equal(2.9, end, 3);
    }

    [Fact]
    public void TreatsAnUnclosedSilenceAsTrailingPadding()
    {
        var log = "silence_start: 0\nsilence_end: 0.8\nsilence_start: 2.3\n";

        var (start, end) = SilenceTrimmer.FindSpeechWindow(log, 2.9);

        Assert.InRange(start, 0.75, 0.8);
        Assert.InRange(end, 2.3, 2.4);
    }

    [Fact]
    public void LeavesAFileWithNoPaddingUntouched()
    {
        // Silence in the middle of a sentence is breathing, not padding.
        var log = "silence_start: 1.2\nsilence_end: 1.35\n";

        var (start, end) = SilenceTrimmer.FindSpeechWindow(log, 2.9);

        Assert.Equal(0, start);
        Assert.Equal(2.9, end, 3);
    }

    [Fact]
    public void RefusesToInvertTheWindow()
    {
        // A file that is silent end to end must come back whole rather than as nothing.
        var log = "silence_start: 0\nsilence_end: 3.0\nsilence_start: 0.1\n";

        var (start, end) = SilenceTrimmer.FindSpeechWindow(log, 2.9);

        Assert.True(end > start);
    }
}

public class BrandKitTests
{
    [Fact]
    public void AppliesOnlyTheFieldsTheKitCarries()
    {
        var creative = new JobCreative { Watermark = "@set-by-hand", AccentColor = "#111111" };
        var kit = new BrandKit { Name = "house", AccentColor = "#F4C430" };

        kit.ApplyTo(creative);

        Assert.Equal("#F4C430", creative.AccentColor);
        // The kit has no watermark of its own, so it must not erase one already set.
        Assert.Equal("@set-by-hand", creative.Watermark);
        Assert.Equal("house", creative.BrandKit);
    }

    [Fact]
    public void RoundTripsAJobsLookThroughAKit()
    {
        var source = new JobCreative
        {
            AccentColor = "#00A6A6",
            CaptionStyle = "karaoke",
            Watermark = "@lanshu",
            CaptionFont = "Inter",
        };
        source.Intro.Enabled = true;
        source.Intro.Title = "Field notes";

        var target = new JobCreative();
        BrandKitService.FromCreative("captured", source).ApplyTo(target);

        Assert.Equal("#00A6A6", target.AccentColor);
        Assert.Equal("karaoke", target.CaptionStyle);
        Assert.Equal("Inter", target.CaptionFont);
        Assert.True(target.Intro.Enabled);
        Assert.Equal("Field notes", target.Intro.Title);
    }

    [Fact]
    public void CloningACardBreaksTheReferenceToTheKit()
    {
        var kit = new BrandKit { Name = "house" };
        kit.Intro.Title = "Original";

        var creative = new JobCreative();
        kit.ApplyTo(creative);
        creative.Intro.Title = "Changed on the job";

        Assert.Equal("Original", kit.Intro.Title);
    }
}

public class CaptionStyleTests
{
    private static CaptionPlan PlanWithMeasuredWords()
    {
        var phrase = new CaptionPhrase
        {
            Text = "hold something back",
            StartSeconds = 1.0,
            EndSeconds = 2.5,
            Words =
            {
                new WordTiming { Word = "hold", StartSeconds = 1.0, EndSeconds = 1.4 },
                new WordTiming { Word = "something", StartSeconds = 1.5, EndSeconds = 2.0 },
                new WordTiming { Word = "back", StartSeconds = 2.1, EndSeconds = 2.4 },
            },
        };

        return new CaptionPlan { Phrases = { phrase }, WordTimingsAreMeasured = true };
    }

    private static CaptionStyleOptions Options(string style) => new()
    {
        Width = 1080,
        Height = 1920,
        CaptionStyle = style,
        CalloutsEnabled = false,
    };

    [Fact]
    public void KaraokeSweepTilesTheWholeEvent()
    {
        var ass = AssWriter.Build(PlanWithMeasuredWords(), Options("karaoke"));

        var line = ass.Split('\n').Single(candidate => candidate.Contains("CaptionKaraoke,,"));
        var totalCentiseconds = System.Text.RegularExpressions.Regex
            .Matches(line, @"\\kf(\d+)")
            .Select(match => int.Parse(match.Groups[1].Value))
            .Sum();

        // 1.0s to 2.5s is 150 centiseconds; the sweep must finish exactly when the line does.
        Assert.Equal(150, totalCentiseconds);
    }

    [Fact]
    public void KaraokeFallsBackWhenTimingsAreOnlyEstimated()
    {
        var plan = PlanWithMeasuredWords();
        plan.WordTimingsAreMeasured = false;

        var ass = AssWriter.Build(plan, Options("karaoke"));

        // A sweep driven by guessed timings drifts off the voice, which looks worse than none.
        Assert.DoesNotContain("CaptionKaraoke,,", ass);
        Assert.Contains(",Caption,,", ass);
    }

    [Fact]
    public void BoxedUsesAnOpaquePlateNotATransparentOne()
    {
        var ass = AssWriter.Build(PlanWithMeasuredWords(), Options("boxed"));

        Assert.Contains("CaptionBoxed,,", ass);
        var style = ass.Split('\n').Single(line => line.StartsWith("Style: CaptionBoxed", StringComparison.Ordinal));

        // ASS alpha is inverted: &H00 is opaque and &HFF is invisible, so the plate needs a low byte.
        var boxColour = style.Split(',')[5];
        var alpha = Convert.ToInt32(boxColour.Substring(2, 2), 16);
        Assert.True(alpha < 0x40, $"box alpha {alpha:X2} is too transparent to read as a plate");
    }

    [Fact]
    public void PopStyleBreaksAPhraseIntoShortChunks()
    {
        var chunks = AssWriter.ChunkForPop(PlanWithMeasuredWords().Phrases[0]);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("hold something", chunks[0].Text);
        Assert.Equal("back", chunks[1].Text);
        // The last chunk must run to the phrase end so nothing blinks out early.
        Assert.Equal(2.5, chunks[^1].End, 3);
    }

    [Fact]
    public void PopStyleDividesEvenlyWithoutMeasuredWords()
    {
        var phrase = new CaptionPhrase
        {
            Text = "one two three four",
            StartSeconds = 0,
            EndSeconds = 2.0,
        };

        var chunks = AssWriter.ChunkForPop(phrase);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(2.0, chunks[^1].End, 3);
    }

    [Fact]
    public void AnUnknownStyleNameFallsBackToClassic()
    {
        var ass = AssWriter.Build(PlanWithMeasuredWords(), Options("neon-explosion"));

        Assert.Contains(",Caption,,", ass);
    }
}

public class CardTests
{
    private static JobCreative Creative() => new() { Width = 1080, Height = 1920, AccentColor = "#F4C430" };

    [Fact]
    public void TitleAndSubtitleShareOneCentredEventSoTheyCannotOverlap()
    {
        var card = new CardSettings { Title = "Measuring What Changed", Subtitle = "field notes" };

        var ass = CardService.BuildCardAss(card, Creative(), 1.6);

        var dialogues = ass.Split('\n').Where(line => line.StartsWith("Dialogue:", StringComparison.Ordinal)).ToList();
        Assert.Single(dialogues);
        Assert.Contains("\\an5", dialogues[0]);
        Assert.Contains("FIELD NOTES", dialogues[0]);
        Assert.Contains("Measuring What Changed", dialogues[0]);
    }

    [Fact]
    public void ACardWithOnlyATitleStillRenders()
    {
        var ass = CardService.BuildCardAss(new CardSettings { Title = "Subscribe" }, Creative(), 1.2);

        Assert.Single(ass.Split('\n'), line => line.StartsWith("Dialogue:", StringComparison.Ordinal));
    }

    [Fact]
    public void ACardWithNoCopyEmitsNoDialogue()
    {
        var ass = CardService.BuildCardAss(new CardSettings(), Creative(), 1.2);

        Assert.DoesNotContain("Dialogue:", ass);
    }

    [Theory]
    [InlineData("#0B0F14", "0x0B0F14")]
    [InlineData("0B0F14", "0x0B0F14")]
    [InlineData("", "0x0B0F14")]
    [InlineData("not-a-colour", "0x0B0F14")]
    public void ColoursAreTranslatedIntoWhatFfmpegAccepts(string input, string expected)
    {
        Assert.Equal(expected, CardService.ToFfmpegColor(input));
    }

    [Fact]
    public void CardsAreOnlyConsideredEnabledWhenTheyHaveSomethingToShow()
    {
        var creative = Creative();
        creative.Intro.Enabled = true;

        // Enabled with no title, subtitle or logo would render an empty coloured hold.
        Assert.False(CardService.AnyEnabled(creative));

        creative.Intro.Title = "Field notes";
        Assert.True(CardService.AnyEnabled(creative));
    }
}

public class WaveformPeaksTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            BitConverter.GetBytes(samples[index]).CopyTo(bytes, index * 2);
        }

        return bytes;
    }

    [Fact]
    public void SurvivesAFullScaleNegativeSample()
    {
        // Math.Abs(short) throws on short.MinValue, and normalized narration reaches full scale.
        var waveform = WaveformPeaks.FromPcm(Pcm(short.MinValue, 0, 100, -200), 8000, 2);

        Assert.Equal(2, waveform.Peaks.Count);
        Assert.Equal(1.0, waveform.Peaks[0], 3);
    }

    [Fact]
    public void KeepsThePeakOfEachBucketNotItsMean()
    {
        // Averaging over a bucket flattens the consonants that make phrase boundaries visible.
        var waveform = WaveformPeaks.FromPcm(Pcm(0, 0, 0, 16384), 8000, 1);

        Assert.Equal(0.5, waveform.Peaks[0], 2);
    }

    [Fact]
    public void ReportsTheRealDuration()
    {
        var waveform = WaveformPeaks.FromPcm(Pcm(new short[8000]), 8000, 10);

        Assert.Equal(1.0, waveform.DurationSeconds, 3);
    }

    [Fact]
    public void HandlesMoreBucketsThanSamplesWithoutPunchingHoles()
    {
        var waveform = WaveformPeaks.FromPcm(Pcm(9000, 12000), 8000, 8);

        Assert.Equal(8, waveform.Peaks.Count);
        Assert.All(waveform.Peaks, peak => Assert.True(peak > 0));
    }

    [Fact]
    public void EmptyAudioIsEmptyRatherThanAnException()
    {
        Assert.Empty(WaveformPeaks.FromPcm(Array.Empty<byte>(), 8000, 10).Peaks);
    }
}
