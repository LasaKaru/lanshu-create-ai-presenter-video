using System.Text;
using System.Text.RegularExpressions;

namespace Lanshu.Presenter.Core.Content;

public sealed record FillerTrimResult(string Text, int Removed)
{
    public bool Changed => Removed > 0;
}

/// <summary>
/// Drops filler words from narration before it is spoken.
///
/// This works on the script rather than on the audio, and that is the point: a speech engine
/// says exactly what it is given, so a filler in the output was a filler in the input. Cutting
/// it here means the captions, the keyword anchors and the chapter times are all built from the
/// same trimmed wording, with nothing to re-align afterwards. Excising "um" from a finished
/// waveform would leave every one of those out of sync.
/// </summary>
public static class FillerTrimmer
{
    /// <summary>
    /// Sounds, not words. These are never part of a sentence's meaning, so they can go wherever
    /// they appear.
    /// </summary>
    private static readonly string[] Interjections =
    {
        "umm", "uhh", "erm", "hmm", "mhm", "um", "uh", "eh", "er", "ah",
    };

    /// <summary>
    /// Phrases that are fillers only when they interrupt a sentence. "You know, that gap costs
    /// money" is padding; "do you know the answer" is the sentence. The difference is punctuation,
    /// so these are cut only when a comma sets them off — anything else is the writer's meaning,
    /// and rewriting that is not trimming.
    ///
    /// Hedges like "just", "really", "sort of" and "kind of" are deliberately absent for the same
    /// reason: they change emphasis, and "a kind of fur" is not a filler at all.
    /// </summary>
    private static readonly string[] DiscourseMarkers = { "you know", "i mean" };

    private static readonly string Markers = string.Join('|', DiscourseMarkers);

    private static readonly Regex InterjectionPattern = new(
        @"(?<![\p{L}\p{N}])(?:" + string.Join('|', Interjections) + @")(?![\p{L}\p{N}])\s*,?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Three shapes, in the order the alternation must try them. An interruption in the middle of
    /// a sentence owns the commas on *both* sides of it — dropping only the trailing one leaves
    /// "The baseline, is the whole point" — so that form has to match before the others get a
    /// chance at the same text.
    /// </summary>
    private static readonly Regex DiscoursePattern = new(
        @"[ \t]*,[ \t]*(?:" + Markers + @")(?![\p{L}\p{N}])[ \t]*,[ \t]*"          // mid-sentence
        + @"|(?<![\p{L}\p{N}])(?:" + Markers + @")(?![\p{L}\p{N}])[ \t]*,[ \t]*"    // sentence-initial
        + @"|[ \t]*,[ \t]*(?:" + Markers + @")(?![\p{L}\p{N}])",                    // trailing
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([,.;:!?])", RegexOptions.Compiled);

    private static readonly Regex DoubledPunctuation = new(@"([,;:])[ \t]*[,;:]+", RegexOptions.Compiled);

    public static FillerTrimResult Trim(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new FillerTrimResult(text ?? string.Empty, 0);
        }

        var (afterInterjections, interjections) = Remove(text, InterjectionPattern);
        var (cleaned, markers) = Remove(afterInterjections, DiscoursePattern);
        var removed = interjections + markers;

        if (removed == 0)
        {
            return new FillerTrimResult(text, 0);
        }

        cleaned = DoubledPunctuation.Replace(cleaned, "$1");
        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
        cleaned = Spaces.Replace(cleaned, " ");

        return new FillerTrimResult(cleaned.Trim(), removed);
    }

    /// <summary>
    /// Removes every match, capitalizing whatever now opens a sentence. A filler that opened one
    /// leaves the next word carrying the capital, and "You know, that gap costs money" would
    /// otherwise become a sentence starting with a lowercase "that".
    /// </summary>
    private static (string Text, int Removed) Remove(string text, Regex pattern)
    {
        var matches = pattern.Matches(text);
        if (matches.Count == 0)
        {
            return (text, 0);
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        foreach (Match match in matches)
        {
            builder.Append(text, cursor, match.Index - cursor);
            cursor = match.Index + match.Length;

            if (!StartsSentence(builder)
                && builder.Length > 0
                && !char.IsWhiteSpace(builder[^1])
                && cursor < text.Length
                && !char.IsWhiteSpace(text[cursor])
                && text[cursor] is not (',' or '.' or '!' or '?' or ';' or ':'))
            {
                builder.Append(' ');
            }

            if (StartsSentence(builder) && cursor < text.Length)
            {
                // Skip whatever whitespace the match left behind before the next word.
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                {
                    cursor++;
                }

                if (cursor < text.Length && char.IsLower(text[cursor]))
                {
                    builder.Append(char.ToUpperInvariant(text[cursor]));
                    cursor++;
                }
            }
        }

        builder.Append(text, cursor, text.Length - cursor);
        return (builder.ToString(), matches.Count);
    }

    /// <summary>True when the next character written would begin a sentence.</summary>
    private static bool StartsSentence(StringBuilder builder)
    {
        for (var index = builder.Length - 1; index >= 0; index--)
        {
            var character = builder[index];
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            return character is '.' or '!' or '?' or ':' or ';';
        }

        return true;
    }
}
