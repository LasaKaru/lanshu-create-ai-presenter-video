using Lanshu.Presenter.Core.Asr;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class VerificationTests
{
    private static AsrResult Heard(string text) => new()
    {
        Provider = "test",
        Text = text,
        WordTimingsAreMeasured = true,
    };

    [Fact]
    public void MatchingTranscriptPasses()
    {
        var report = AsrVerification.Compare(
            "Lock the narration first, then measure it once.",
            Heard("Lock the narration first then measure it once"),
            transcribed: true);

        Assert.True(report.Checked);
        Assert.Equal(1.0, report.MatchRatio, 3);
        Assert.Empty(report.Omissions);
        Assert.Empty(report.Additions);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void DroppedTailSpeechIsReported()
    {
        var report = AsrVerification.Compare(
            "Lock the narration first, then measure it once and publish.",
            Heard("Lock the narration first"),
            transcribed: true);

        Assert.True(report.MatchRatio < 0.9);
        Assert.Contains("publish", report.Omissions);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public void RepeatedSpeechShowsAsAnAddition()
    {
        var report = AsrVerification.Compare(
            "Measure it once.",
            Heard("Measure measure it once"),
            transcribed: true);

        Assert.Contains("measure", report.Additions);
    }

    [Fact]
    public void SkippingTranscriptionSaysSoInsteadOfClaimingAPass()
    {
        var report = AsrVerification.Compare("anything", Heard(string.Empty), transcribed: false);

        Assert.False(report.Checked);
        Assert.Single(report.Warnings);
        Assert.Contains("not machine-verified", report.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MeasuredTimingsAreMappedOntoScriptTokens()
    {
        var segments = new List<Lanshu.Presenter.Core.Voice.NarrationSegment>
        {
            new(0, 0, "Lock the narration.", "a.wav", 0, 2.0),
        };

        var measured = new AsrResult
        {
            Provider = "whisper",
            WordTimingsAreMeasured = true,
            Words =
            {
                new WordTiming { Word = "Lock", StartSeconds = 0.10, EndSeconds = 0.50 },
                new WordTiming { Word = "the", StartSeconds = 0.55, EndSeconds = 0.70 },
                new WordTiming { Word = "narration", StartSeconds = 0.75, EndSeconds = 1.60 },
            },
        };

        var merged = ProportionalAligner.MergeMeasuredTimings(measured, segments);

        Assert.True(merged.WordTimingsAreMeasured);
        Assert.Equal(3, merged.Words.Count);

        // Boundaries come from the measured window; the words themselves come from the script.
        Assert.Equal(0.10, merged.Words[0].StartSeconds, 2);
        Assert.Equal(1.60, merged.Words[^1].EndSeconds, 2);
        Assert.Equal(new[] { "Lock", "the", "narration" }, merged.Words.Select(word => word.Word));
    }

    [Fact]
    public void MissingMeasuredWordsFallBackToTheAligner()
    {
        var segments = new List<Lanshu.Presenter.Core.Voice.NarrationSegment>
        {
            new(0, 0, "Lock the narration.", "a.wav", 0, 2.0),
        };

        var merged = ProportionalAligner.MergeMeasuredTimings(new AsrResult { Provider = "whisper" }, segments);
        Assert.Equal(3, merged.Words.Count);
        Assert.False(merged.WordTimingsAreMeasured);
    }
}
