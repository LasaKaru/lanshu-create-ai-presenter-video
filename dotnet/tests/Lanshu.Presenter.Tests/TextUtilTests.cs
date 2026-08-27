using Lanshu.Presenter.Core.Util;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class TextUtilTests
{
    [Fact]
    public void DetectsChineseNarration()
    {
        Assert.True(TextUtil.IsCjk("把这几点串起来就不难了"));
        Assert.Equal("zh", TextUtil.DetectLanguage("把这几点串起来"));
        Assert.False(TextUtil.IsCjk("Lock the narration first."));
        Assert.Equal("en", TextUtil.DetectLanguage("Lock the narration first."));
    }

    [Fact]
    public void StripsMarkdownAndStageDirections()
    {
        var spoken = TextUtil.ToSpokenText("""
            ## Hook

            - **Lock** the narration first. (pause)

            NARRATOR: Then measure it.
            """);

        Assert.DoesNotContain("**", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("(pause)", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("NARRATOR", spoken, StringComparison.Ordinal);
        Assert.Contains("Lock the narration first.", spoken, StringComparison.Ordinal);
        Assert.Contains("Then measure it.", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitsSentencesAcrossBothPunctuationSets()
    {
        Assert.Equal(2, TextUtil.SplitSentences("One clock. Measured once.").Count);
        Assert.Equal(2, TextUtil.SplitSentences("先锁定文案。再测量时长。").Count);
    }

    [Fact]
    public void SegmentsStayUnderTheCharacterBudget()
    {
        var narration = string.Join(" ", Enumerable.Repeat("This is a reasonably long sentence about timing.", 20));
        var segments = TextUtil.BuildSegments(narration, maxCharacters: 120);

        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.True(segment.Length <= 120, $"segment was {segment.Length} chars"));
    }

    [Fact]
    public void SegmentsSplitASentenceLongerThanTheBudget()
    {
        var single = new string('a', 60) + ", " + new string('b', 60) + ", " + new string('c', 60) + ".";
        var segments = TextUtil.BuildSegments(single, maxCharacters: 80);

        Assert.True(segments.Count >= 3);
        Assert.All(segments, segment => Assert.True(segment.Length <= 80));
    }

    [Fact]
    public void TokenizesCjkPerCharacterAndLatinPerWord()
    {
        Assert.Equal(new[] { "lock", "the", "narration" }, TextUtil.Tokenize("Lock, the narration!").Select(t => t.ToLowerInvariant()));
        Assert.Equal(4, TextUtil.Tokenize("锁定文案").Count);
    }

    [Fact]
    public void SpokenDurationGrowsWithLength()
    {
        var shortText = TextUtil.EstimateSpokenSeconds("One clock.");
        var longText = TextUtil.EstimateSpokenSeconds(string.Join(" ", Enumerable.Repeat("One clock.", 30)));
        Assert.True(longText > shortText * 10);
    }
}
