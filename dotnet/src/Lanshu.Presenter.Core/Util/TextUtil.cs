using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lanshu.Presenter.Core.Util;

public static class TextUtil
{
    private static readonly Regex MarkdownNoise = new(
        @"^\s{0,3}(#{1,6}\s+|[-*+]\s+|\d+[.)]\s+|>\s?)",
        RegexOptions.Compiled);

    private static readonly Regex Emphasis = new(@"(\*\*|__|\*|`)", RegexOptions.Compiled);

    private static readonly Regex SquareBrackets = new(@"\[(.*?)\]\((.*?)\)", RegexOptions.Compiled);

    private static readonly Regex StageDirection = new(@"[（(\[]\s*(?:pause|beat|smile|gesture|停顿|微笑|手势)[^）)\]]*[）)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"[ \t]+", RegexOptions.Compiled);

    /// <summary>True when the text is mostly CJK, which changes sentence splitting and caption width.</summary>
    public static bool IsCjk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cjk = 0;
        var letters = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune))
            {
                letters++;
                var value = rune.Value;
                if ((value >= 0x4E00 && value <= 0x9FFF)
                    || (value >= 0x3400 && value <= 0x4DBF)
                    || (value >= 0x3040 && value <= 0x30FF)
                    || (value >= 0xAC00 && value <= 0xD7AF))
                {
                    cjk++;
                }
            }
        }

        return letters > 0 && cjk * 2 >= letters;
    }

    public static string DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "en";
        }

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value >= 0x3040 && value <= 0x30FF)
            {
                return "ja";
            }

            if (value >= 0xAC00 && value <= 0xD7AF)
            {
                return "ko";
            }
        }

        return IsCjk(text) ? "zh" : "en";
    }

    /// <summary>Strips markdown, stage directions and speaker labels so only spoken words remain.</summary>
    public static string ToSpokenText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                builder.AppendLine();
                continue;
            }

            if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            line = MarkdownNoise.Replace(line, string.Empty);
            line = SquareBrackets.Replace(line, "$1");
            line = Emphasis.Replace(line, string.Empty);
            line = StageDirection.Replace(line, string.Empty);

            // Drop "NARRATOR:" / "旁白：" style labels but keep sentences that merely contain a colon.
            var colon = line.IndexOfAny(new[] { ':', '：' });
            if (colon > 0 && colon <= 24 && line[..colon].All(character =>
                    char.IsUpper(character) || character == ' ' || character == '_' || IsCjk(character.ToString())))
            {
                line = line[(colon + 1)..];
            }

            line = Whitespace.Replace(line, " ").Trim();
            if (line.Length > 0)
            {
                builder.AppendLine(line);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Splits into sentence-ish units for both narration segments and caption phrases.</summary>
    public static IReadOnlyList<string> SplitSentences(string text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        var builder = new StringBuilder();
        var enumerator = text.Replace("\r\n", "\n").GetEnumerator();
        while (enumerator.MoveNext())
        {
            var character = enumerator.Current;
            if (character == '\n')
            {
                Flush(builder, results);
                continue;
            }

            builder.Append(character);
            if (IsSentenceEnd(character))
            {
                Flush(builder, results);
            }
        }

        Flush(builder, results);
        return results;
    }

    private static bool IsSentenceEnd(char character) =>
        character is '.' or '!' or '?' or '。' or '！' or '？' or '…' or ';' or '；';

    private static void Flush(StringBuilder builder, List<string> results)
    {
        var value = builder.ToString().Trim();
        builder.Clear();
        if (value.Length > 0)
        {
            results.Add(value);
        }
    }

    /// <summary>
    /// Groups sentences into synthesis segments. Long segments drift on most TTS engines and
    /// short ones waste requests, so aim for a readable middle.
    /// </summary>
    public static IReadOnlyList<string> BuildSegments(string narration, int maxCharacters = 240)
    {
        var sentences = SplitSentences(narration);
        var segments = new List<string>();
        var current = new StringBuilder();

        foreach (var sentence in sentences)
        {
            if (current.Length > 0 && current.Length + sentence.Length + 1 > maxCharacters)
            {
                segments.Add(current.ToString().Trim());
                current.Clear();
            }

            if (sentence.Length > maxCharacters)
            {
                if (current.Length > 0)
                {
                    segments.Add(current.ToString().Trim());
                    current.Clear();
                }

                foreach (var chunk in HardWrap(sentence, maxCharacters))
                {
                    segments.Add(chunk);
                }

                continue;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString().Trim());
        }

        return segments.Where(segment => segment.Length > 0).ToList();
    }

    private static IEnumerable<string> HardWrap(string text, int maxCharacters)
    {
        var remaining = text;
        while (remaining.Length > maxCharacters)
        {
            var cut = remaining.LastIndexOfAny(new[] { ',', '，', '、', ' ', '—' }, maxCharacters - 1);
            if (cut < maxCharacters / 2)
            {
                cut = maxCharacters - 1;
            }

            yield return remaining[..(cut + 1)].Trim();
            remaining = remaining[(cut + 1)..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    /// <summary>Rough spoken-duration estimate used before any audio exists.</summary>
    public static double EstimateSpokenSeconds(string text, double rate = 1.0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (rate <= 0)
        {
            rate = 1;
        }

        if (IsCjk(text))
        {
            var characters = text.Count(character => !char.IsWhiteSpace(character));
            return characters / (5.2 * rate);
        }

        var words = text.Split(
            new[] { ' ', '\n', '\t', '\r' },
            StringSplitOptions.RemoveEmptyEntries).Length;
        return words / (2.6 * rate);
    }

    /// <summary>Counts the units the caption layer treats as one "word" for timing.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                FlushToken(builder, tokens);
                continue;
            }

            if (IsCjkRune(rune))
            {
                FlushToken(builder, tokens);
                tokens.Add(rune.ToString());
                continue;
            }

            if (Rune.IsPunctuation(rune) || Rune.IsSymbol(rune))
            {
                if (rune.Value is '\'' or '-' or '’')
                {
                    builder.Append(rune);
                    continue;
                }

                FlushToken(builder, tokens);
                continue;
            }

            builder.Append(rune);
        }

        FlushToken(builder, tokens);
        return tokens;
    }

    private static bool IsCjkRune(Rune rune)
    {
        var value = rune.Value;
        return (value >= 0x4E00 && value <= 0x9FFF)
               || (value >= 0x3400 && value <= 0x4DBF)
               || (value >= 0x3040 && value <= 0x30FF)
               || (value >= 0xAC00 && value <= 0xD7AF);
    }

    private static void FlushToken(StringBuilder builder, List<string> tokens)
    {
        if (builder.Length == 0)
        {
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
    }

    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..Math.Max(0, maxLength - 1)] + "…";
    }

    public static string TitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsCjk(text))
        {
            return text;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
    }
}
