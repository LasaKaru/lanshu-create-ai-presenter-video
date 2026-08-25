using Lanshu.Presenter.Core.Util;
using Lanshu.Presenter.Core.Voice;

namespace Lanshu.Presenter.Core.Asr;

/// <summary>
/// Derives word timings without a transcription service. Because narration is synthesized one
/// segment at a time, the exact text and the exact measured duration of every segment are already
/// known — the only thing estimated is where each word sits inside its own segment.
/// That is accurate enough for captions and keyword anchors, and it needs no network.
/// </summary>
public static class ProportionalAligner
{
    /// <summary>Leading and trailing silence a TTS engine usually pads onto a segment.</summary>
    private const double EdgeTrimSeconds = 0.06;

    public static AsrResult Align(IReadOnlyList<NarrationSegment> segments)
    {
        var result = new AsrResult
        {
            Provider = "proportional-aligner",
            Model = "built-in",
            WordTimingsAreMeasured = false,
            Text = string.Join(" ", segments.Select(segment => segment.Text.Trim())),
        };

        foreach (var segment in segments)
        {
            foreach (var timing in AlignSegment(segment.Text, segment.StartSeconds, segment.DurationSeconds))
            {
                result.Words.Add(timing);
            }
        }

        return result;
    }

    public static IReadOnlyList<WordTiming> AlignSegment(string text, double startSeconds, double durationSeconds)
    {
        var tokens = TextUtil.Tokenize(text);
        var timings = new List<WordTiming>(tokens.Count);
        if (tokens.Count == 0 || durationSeconds <= 0)
        {
            return timings;
        }

        var trim = Math.Min(EdgeTrimSeconds, durationSeconds * 0.05);
        var usable = Math.Max(0.05, durationSeconds - (trim * 2));
        var origin = startSeconds + trim;

        // Weight by token length so a long word occupies more of the segment than a short one.
        var weights = tokens.Select(TokenWeight).ToArray();
        var total = weights.Sum();
        if (total <= 0)
        {
            total = tokens.Count;
            weights = Enumerable.Repeat(1.0, tokens.Count).ToArray();
        }

        var cursor = origin;
        for (var index = 0; index < tokens.Count; index++)
        {
            var span = usable * (weights[index] / total);
            timings.Add(new WordTiming
            {
                Word = tokens[index],
                StartSeconds = Math.Round(cursor, 3),
                EndSeconds = Math.Round(cursor + span, 3),
            });
            cursor += span;
        }

        return timings;
    }

    private static double TokenWeight(string token)
    {
        if (TextUtil.IsCjk(token))
        {
            // One CJK character carries roughly the weight of a short English word.
            return 2.6;
        }

        return 1.0 + token.Length * 0.55;
    }

    /// <summary>
    /// Re-times measured ASR words onto the intended script text. ASR output is used for
    /// verification; the words shown on screen always come from the approved script.
    /// </summary>
    public static AsrResult MergeMeasuredTimings(AsrResult measured, IReadOnlyList<NarrationSegment> segments)
    {
        if (measured.Words.Count == 0)
        {
            return Align(segments);
        }

        var merged = new AsrResult
        {
            Provider = measured.Provider,
            Model = measured.Model,
            Text = measured.Text,
            WordTimingsAreMeasured = true,
        };

        var measuredWords = measured.Words.OrderBy(word => word.StartSeconds).ToList();
        var consumed = 0;

        foreach (var segment in segments)
        {
            var scriptTokens = TextUtil.Tokenize(segment.Text);
            if (scriptTokens.Count == 0)
            {
                continue;
            }

            // Take the measured words that fall inside this segment's audio window.
            var window = new List<WordTiming>();
            while (consumed < measuredWords.Count
                   && measuredWords[consumed].StartSeconds < segment.EndSeconds + 0.05)
            {
                if (measuredWords[consumed].EndSeconds >= segment.StartSeconds - 0.05)
                {
                    window.Add(measuredWords[consumed]);
                }

                consumed++;
            }

            if (window.Count == 0)
            {
                foreach (var timing in AlignSegment(segment.Text, segment.StartSeconds, segment.DurationSeconds))
                {
                    merged.Words.Add(timing);
                }

                continue;
            }

            // Map script tokens onto the measured window proportionally: the boundaries are real,
            // the within-window distribution is interpolated.
            var windowStart = window[0].StartSeconds;
            var windowEnd = window[^1].EndSeconds;
            var span = Math.Max(0.05, windowEnd - windowStart);
            var weights = scriptTokens.Select(token => TextUtil.IsCjk(token) ? 2.6 : 1.0 + token.Length * 0.55).ToArray();
            var total = weights.Sum();
            var cursor = windowStart;

            for (var index = 0; index < scriptTokens.Count; index++)
            {
                var slice = span * (weights[index] / total);
                merged.Words.Add(new WordTiming
                {
                    Word = scriptTokens[index],
                    StartSeconds = Math.Round(cursor, 3),
                    EndSeconds = Math.Round(cursor + slice, 3),
                });
                cursor += slice;
            }
        }

        return merged;
    }
}
