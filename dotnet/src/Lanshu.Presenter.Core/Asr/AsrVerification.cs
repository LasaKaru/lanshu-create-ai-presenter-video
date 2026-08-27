using System.Text;
using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Asr;

public sealed class AsrVerificationReport
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("word_timings_are_measured")]
    public bool WordTimingsAreMeasured { get; set; }

    [JsonPropertyName("checked")]
    public bool Checked { get; set; }

    [JsonPropertyName("match_ratio")]
    public double MatchRatio { get; set; }

    [JsonPropertyName("intended_tokens")]
    public int IntendedTokens { get; set; }

    [JsonPropertyName("heard_tokens")]
    public int HeardTokens { get; set; }

    [JsonPropertyName("omissions")]
    public List<string> Omissions { get; set; } = new();

    [JsonPropertyName("additions")]
    public List<string> Additions { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();

    [JsonPropertyName("intended_text")]
    public string IntendedText { get; set; } = string.Empty;

    [JsonPropertyName("heard_text")]
    public string HeardText { get; set; } = string.Empty;

    [JsonPropertyName("words")]
    public List<WordTiming> Words { get; set; } = new();
}

/// <summary>
/// Compares what the script said with what the final audio actually contains, which is the
/// audio acceptance gate in qa-recovery.md. Skipped cleanly when no transcription ran.
/// </summary>
public static class AsrVerification
{
    public static AsrVerificationReport Compare(string intendedText, AsrResult result, bool transcribed)
    {
        var report = new AsrVerificationReport
        {
            Provider = result.Provider,
            Model = result.Model,
            WordTimingsAreMeasured = result.WordTimingsAreMeasured,
            Checked = transcribed,
            IntendedText = intendedText,
            HeardText = result.Text,
            Words = result.Words,
        };

        if (!transcribed)
        {
            report.MatchRatio = 1;
            report.Warnings.Add(
                "No transcription service ran. Caption timings come from the proportional aligner and the audio was not machine-verified against the script. Listen to the narration before delivery.");
            return report;
        }

        var intended = Normalize(intendedText);
        var heard = Normalize(result.Text);
        report.IntendedTokens = intended.Count;
        report.HeardTokens = heard.Count;

        var matched = LongestCommonSubsequence(intended, heard);
        report.MatchRatio = intended.Count == 0 ? 1 : Math.Round((double)matched.Count / intended.Count, 4);

        var heardCounts = ToCounts(heard);
        var intendedCounts = ToCounts(intended);

        foreach (var (token, count) in intendedCounts)
        {
            var heardCount = heardCounts.TryGetValue(token, out var value) ? value : 0;
            if (heardCount < count)
            {
                report.Omissions.Add(token);
            }
        }

        foreach (var (token, count) in heardCounts)
        {
            var intendedCount = intendedCounts.TryGetValue(token, out var value) ? value : 0;
            if (intendedCount < count)
            {
                report.Additions.Add(token);
            }
        }

        if (report.MatchRatio < 0.90)
        {
            report.Warnings.Add(
                $"transcription matched only {report.MatchRatio:P0} of the script; review names, numbers, and any dropped tail speech");
        }

        if (report.Omissions.Count > 0)
        {
            report.Warnings.Add(
                "possible omissions: " + string.Join(", ", report.Omissions.Take(20)));
        }

        if (report.Additions.Count > 0)
        {
            report.Warnings.Add(
                "possible additions or repeats: " + string.Join(", ", report.Additions.Take(20)));
        }

        return report;
    }

    private static List<string> Normalize(string text) =>
        TextUtil.Tokenize(text).Select(token => token.ToLowerInvariant()).ToList();

    private static Dictionary<string, int> ToCounts(IEnumerable<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            counts[token] = counts.TryGetValue(token, out var value) ? value + 1 : 1;
        }

        return counts;
    }

    /// <summary>
    /// Standard LCS, bounded so a long narration cannot blow up the quadratic table.
    /// </summary>
    private static List<string> LongestCommonSubsequence(List<string> left, List<string> right)
    {
        const int limit = 2000;
        if (left.Count > limit || right.Count > limit)
        {
            var trimmedLeft = left.Take(limit).ToList();
            var trimmedRight = right.Take(limit).ToList();
            return LongestCommonSubsequence(trimmedLeft, trimmedRight);
        }

        var table = new int[left.Count + 1, right.Count + 1];
        for (var i = 1; i <= left.Count; i++)
        {
            for (var j = 1; j <= right.Count; j++)
            {
                table[i, j] = left[i - 1] == right[j - 1]
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        var sequence = new List<string>();
        var x = left.Count;
        var y = right.Count;
        while (x > 0 && y > 0)
        {
            if (left[x - 1] == right[y - 1])
            {
                sequence.Add(left[x - 1]);
                x--;
                y--;
            }
            else if (table[x - 1, y] >= table[x, y - 1])
            {
                x--;
            }
            else
            {
                y--;
            }
        }

        sequence.Reverse();
        return sequence;
    }

    public static string ToMarkdown(AsrVerificationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Audio verification");
        builder.AppendLine();
        builder.AppendLine($"- provider: `{(string.IsNullOrWhiteSpace(report.Provider) ? "none" : report.Provider)}`");
        builder.AppendLine($"- machine-checked: `{report.Checked}`");
        builder.AppendLine($"- measured word timings: `{report.WordTimingsAreMeasured}`");
        builder.AppendLine($"- match ratio: `{report.MatchRatio:P1}`");
        builder.AppendLine();

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
