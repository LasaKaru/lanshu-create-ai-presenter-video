using Lanshu.Presenter.Core.Asr;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Util;
using Lanshu.Presenter.Core.Voice;

namespace Lanshu.Presenter.Core.Captions;

/// <summary>
/// Turns word timings into short, readable phrases and binds one keyword callout per beat to a
/// real spoken anchor, following the caption and preset rules in editing.md.
/// </summary>
public sealed class CaptionBuilder
{
    /// <summary>Visual lead before the word is spoken, from editing.md's practical baseline.</summary>
    private const double LeadSeconds = 0.040;

    private const double TailHoldSeconds = 0.120;

    private const double MinimumPhraseSeconds = 0.70;

    private const double MaximumPhraseSeconds = 4.20;

    public CaptionPlan Build(
        AsrResult timings,
        ScriptDocument script,
        IReadOnlyList<NarrationSegment> segments,
        bool keywordCalloutsEnabled,
        CaptionLayout layout)
    {
        var plan = new CaptionPlan { WordTimingsAreMeasured = timings.WordTimingsAreMeasured };
        if (timings.Words.Count == 0)
        {
            return plan;
        }

        var cjk = TextUtil.IsCjk(string.Join(string.Empty, timings.Words.Select(word => word.Word)));
        var maxCharacters = layout.MaxCharactersPerPhrase(cjk);

        // Phrase boundaries follow the narration segments, so a caption never straddles a pause.
        var wordIndex = 0;
        foreach (var segment in segments)
        {
            var segmentWordCount = TextUtil.Tokenize(segment.Text).Count;
            var segmentWords = timings.Words
                .Skip(wordIndex)
                .Take(segmentWordCount)
                .ToList();
            wordIndex += segmentWordCount;
            if (segmentWords.Count == 0)
            {
                continue;
            }

            foreach (var phrase in SplitPhrases(segmentWords, segment.Text, maxCharacters, cjk))
            {
                plan.Phrases.Add(phrase);
            }
        }

        // Any words the segment walk did not consume still deserve captions.
        if (wordIndex < timings.Words.Count)
        {
            var remainder = timings.Words.Skip(wordIndex).ToList();
            foreach (var phrase in SplitPhrases(remainder, string.Join(" ", remainder.Select(word => word.Word)), maxCharacters, cjk))
            {
                plan.Phrases.Add(phrase);
            }
        }

        ApplyLeadAndHold(plan.Phrases);

        for (var index = 0; index < plan.Phrases.Count; index++)
        {
            plan.Phrases[index].Index = index;
            plan.Phrases[index].Highlight = PickHighlight(plan.Phrases[index], script, cjk);
        }

        if (keywordCalloutsEnabled)
        {
            plan.Callouts.AddRange(BuildCallouts(plan.Phrases, script, segments));
        }

        return plan;
    }

    private static IEnumerable<CaptionPhrase> SplitPhrases(
        IReadOnlyList<WordTiming> words,
        string sourceText,
        int maxCharacters,
        bool cjk)
    {
        var phrases = new List<CaptionPhrase>();
        var current = new List<WordTiming>();
        var length = 0;

        // Words carry no punctuation, so use the source sentence order to find natural breaks.
        var breakAfter = BreakPositions(sourceText, words.Count);

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var addition = cjk ? word.Word.Length : word.Word.Length + 1;

            var wouldOverflow = current.Count > 0 && length + addition > maxCharacters;
            var tooLong = current.Count > 0
                          && word.EndSeconds - current[0].StartSeconds > MaximumPhraseSeconds;

            if (wouldOverflow || tooLong)
            {
                phrases.Add(ToPhrase(current, cjk));
                current = new List<WordTiming>();
                length = 0;
            }

            current.Add(word);
            length += addition;

            if (breakAfter.Contains(index) && current.Count > 0)
            {
                var span = current[^1].EndSeconds - current[0].StartSeconds;
                if (span >= MinimumPhraseSeconds || index == words.Count - 1)
                {
                    phrases.Add(ToPhrase(current, cjk));
                    current = new List<WordTiming>();
                    length = 0;
                }
            }
        }

        if (current.Count > 0)
        {
            phrases.Add(ToPhrase(current, cjk));
        }

        return phrases;
    }

    /// <summary>Word indexes that end a clause, derived from punctuation in the original text.</summary>
    private static HashSet<int> BreakPositions(string sourceText, int wordCount)
    {
        var positions = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return positions;
        }

        var consumed = 0;
        foreach (var clause in SplitClauses(sourceText))
        {
            var tokens = TextUtil.Tokenize(clause).Count;
            if (tokens == 0)
            {
                continue;
            }

            consumed += tokens;
            if (consumed - 1 < wordCount)
            {
                positions.Add(consumed - 1);
            }
        }

        return positions;
    }

    private static IEnumerable<string> SplitClauses(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '.' or '!' or '?' or ',' or ';' or ':'
                or '。' or '！' or '？' or '，' or '、' or '；' or '：' or '…')
            {
                yield return text[start..(index + 1)];
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static CaptionPhrase ToPhrase(IReadOnlyList<WordTiming> words, bool cjk)
    {
        var separator = cjk ? string.Empty : " ";
        return new CaptionPhrase
        {
            Text = string.Join(separator, words.Select(word => word.Word)).Trim(),
            StartSeconds = words[0].StartSeconds,
            EndSeconds = words[^1].EndSeconds,
            Words = words.ToList(),
        };
    }

    /// <summary>
    /// Applies the small lead and hold margins, but only where the neighbouring phrase leaves room.
    /// </summary>
    private static void ApplyLeadAndHold(List<CaptionPhrase> phrases)
    {
        for (var index = 0; index < phrases.Count; index++)
        {
            var phrase = phrases[index];
            var previousEnd = index == 0 ? 0 : phrases[index - 1].EndSeconds;
            var nextStart = index == phrases.Count - 1 ? double.MaxValue : phrases[index + 1].StartSeconds;

            var start = Math.Max(previousEnd, phrase.StartSeconds - LeadSeconds);
            var end = Math.Min(nextStart, phrase.EndSeconds + TailHoldSeconds);

            phrase.StartSeconds = Math.Round(Math.Max(0, start), 3);
            phrase.EndSeconds = Math.Round(Math.Max(phrase.StartSeconds + 0.20, end), 3);
        }
    }

    /// <summary>Picks the one word worth emphasizing inside a phrase.</summary>
    private static string PickHighlight(CaptionPhrase phrase, ScriptDocument script, bool cjk)
    {
        var keywords = script.Beats
            .Select(beat => beat.Keyword)
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .ToList();

        foreach (var keyword in keywords)
        {
            foreach (var token in TextUtil.Tokenize(keyword))
            {
                // A keyword like "the point" must not highlight "the" every time it appears.
                if (!IsMeaningful(token, cjk))
                {
                    continue;
                }

                if (phrase.Words.Any(word => string.Equals(word.Word, token, StringComparison.OrdinalIgnoreCase)))
                {
                    return token;
                }
            }
        }

        if (cjk)
        {
            return string.Empty;
        }

        // Otherwise emphasize the longest content word, which is usually the informative one.
        return phrase.Words
            .Select(word => word.Word)
            .Where(word => word.Length > 4 && !StopWords.Contains(word.ToLowerInvariant()))
            .OrderByDescending(word => word.Length)
            .FirstOrDefault() ?? string.Empty;
    }

    private static bool IsMeaningful(string token, bool cjk)
    {
        if (cjk)
        {
            return token.Length >= 1;
        }

        return token.Length >= 4 && !StopWords.Contains(token.ToLowerInvariant());
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "also", "because", "been", "before", "being", "between",
        "could", "does", "each", "else", "even", "ever", "every", "first", "from", "going",
        "have", "here", "into", "just", "like", "make", "many", "might",
        "more", "most", "much", "must", "need", "once", "only", "other", "over", "same",
        "should", "some", "such", "than", "that", "their", "them", "then", "there", "these",
        "they", "thing", "things", "think", "this", "those", "through", "under", "until",
        "very", "were", "what", "when", "where", "which", "while", "will", "with", "would",
        "your", "yours",
    };

    /// <summary>
    /// One callout per beat, anchored to the first phrase inside that beat that actually contains
    /// the keyword — or to the beat's opening phrase when the keyword is not spoken verbatim.
    /// Presets rotate so no single card repeats through the video.
    /// </summary>
    private static IEnumerable<KeywordCallout> BuildCallouts(
        IReadOnlyList<CaptionPhrase> phrases,
        ScriptDocument script,
        IReadOnlyList<NarrationSegment> segments)
    {
        var callouts = new List<KeywordCallout>();
        if (phrases.Count == 0)
        {
            return callouts;
        }

        var presetIndex = 0;
        for (var beatIndex = 0; beatIndex < script.Beats.Count; beatIndex++)
        {
            var beat = script.Beats[beatIndex];
            if (string.IsNullOrWhiteSpace(beat.Keyword))
            {
                continue;
            }

            var beatSegments = segments.Where(segment => segment.BeatIndex == beatIndex).ToList();
            if (beatSegments.Count == 0)
            {
                continue;
            }

            var beatStart = beatSegments[0].StartSeconds;
            var beatEnd = beatSegments[^1].EndSeconds;

            var keywordTokens = TextUtil.Tokenize(beat.Keyword)
                .Select(token => token.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

            var anchor = phrases.FirstOrDefault(phrase =>
                              phrase.StartSeconds >= beatStart - 0.2
                              && phrase.StartSeconds <= beatEnd
                              && phrase.Words.Any(word => keywordTokens.Contains(word.Word.ToLowerInvariant())))
                          ?? phrases.FirstOrDefault(phrase => phrase.StartSeconds >= beatStart - 0.2);

            if (anchor is null)
            {
                continue;
            }

            var start = Math.Max(0, anchor.StartSeconds - 0.08);
            var end = Math.Min(beatEnd + 0.4, start + 2.6);
            if (end - start < 1.0)
            {
                end = Math.Min(beatEnd + 0.6, start + 1.4);
            }

            callouts.Add(new KeywordCallout
            {
                Text = beat.Keyword.Trim(),
                Kicker = beat.RoleValue switch
                {
                    BeatRole.Hook => "START HERE",
                    BeatRole.Close => "TAKEAWAY",
                    BeatRole.Synthesis => "PUT TOGETHER",
                    _ => $"0{Math.Min(9, beatIndex)}",
                },
                StartSeconds = Math.Round(start, 3),
                EndSeconds = Math.Round(end, 3),
                Preset = AssWriter.PresetNames[presetIndex++ % AssWriter.PresetNames.Length],
            });
        }

        return callouts;
    }
}
