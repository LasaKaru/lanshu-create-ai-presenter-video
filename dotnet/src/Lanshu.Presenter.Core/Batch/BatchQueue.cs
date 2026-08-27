using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Batch;

/// <summary>One row of the queue: a video someone wants made.</summary>
public sealed class BatchRow
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("script")]
    public string ScriptPath { get; set; } = string.Empty;

    [JsonPropertyName("presenter_image")]
    public string PresenterImage { get; set; } = string.Empty;

    [JsonPropertyName("brand")]
    public string Brand { get; set; } = string.Empty;

    [JsonPropertyName("aspect")]
    public string Aspect { get; set; } = string.Empty;

    [JsonPropertyName("duration_s")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("job_dir")]
    public string JobDirectory { get; set; } = string.Empty;

    /// <summary>pending | running | done | failed | skipped</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;

    [JsonPropertyName("master")]
    public string Master { get; set; } = string.Empty;

    [JsonPropertyName("finished_utc")]
    public string FinishedUtc { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsFinished => Status is "done" or "skipped";
}

public sealed class BatchState
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("created_utc")]
    public string CreatedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    [JsonPropertyName("rows")]
    public List<BatchRow> Rows { get; set; } = new();

    [JsonIgnore]
    public int Done => Rows.Count(row => row.Status == "done");

    [JsonIgnore]
    public int Failed => Rows.Count(row => row.Status == "failed");

    [JsonIgnore]
    public int Remaining => Rows.Count(row => !row.IsFinished);
}

/// <summary>
/// Reads a CSV of videos to make and keeps the outcome of each row next to it.
///
/// The state file is the point. A batch left running overnight will hit something — a provider
/// outage, a bad path, a machine that reboots — and the only version of this feature worth having
/// is one where that costs the failed row and nothing else. Progress is written after every row,
/// so a re-run picks up where it stopped rather than re-rendering what already succeeded.
/// </summary>
public static class BatchQueue
{
    /// <summary>
    /// Parses a CSV with a header row. Column names are matched case-insensitively and unknown
    /// columns are ignored, so a spreadsheet someone keeps for their own reasons still works.
    /// </summary>
    public static BatchState Parse(string csv, string source = "")
    {
        var state = new BatchState { Source = source };
        var lines = SplitLines(csv);
        if (lines.Count == 0)
        {
            return state;
        }

        var header = ParseRow(lines[0]).Select(cell => cell.Trim().ToLowerInvariant()).ToList();
        var index = 0;

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = ParseRow(line);
            string Get(params string[] names)
            {
                foreach (var name in names)
                {
                    var at = header.IndexOf(name);
                    if (at >= 0 && at < cells.Count)
                    {
                        return cells[at].Trim();
                    }
                }

                return string.Empty;
            }

            var row = new BatchRow
            {
                Index = index++,
                Topic = Get("topic", "subject", "title"),
                ScriptPath = Get("script", "script_path", "scriptfile"),
                PresenterImage = Get("presenter_image", "presenter", "image"),
                Brand = Get("brand", "brand_kit", "kit"),
                Aspect = Get("aspect", "ratio"),
                JobDirectory = Get("job_dir", "output", "directory"),
            };

            var duration = Get("duration", "duration_s", "seconds");
            if (double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                row.DurationSeconds = parsed;
            }

            // A row with nothing to say and no image to say it with is not a video.
            if (string.IsNullOrWhiteSpace(row.Topic) && string.IsNullOrWhiteSpace(row.ScriptPath))
            {
                row.Status = "skipped";
                row.Detail = "no topic and no script";
            }

            state.Rows.Add(row);
        }

        return state;
    }

    /// <summary>
    /// Merges a freshly parsed CSV onto saved progress, keyed by row index. Rows that already
    /// finished keep their outcome; a row that failed goes back to pending so a re-run retries it.
    /// </summary>
    public static BatchState Resume(BatchState parsed, BatchState? saved)
    {
        if (saved is null)
        {
            return parsed;
        }

        foreach (var row in parsed.Rows)
        {
            var previous = saved.Rows.FirstOrDefault(candidate => candidate.Index == row.Index);
            if (previous is null || !previous.IsFinished)
            {
                continue;
            }

            row.Status = previous.Status;
            row.Detail = previous.Detail;
            row.Master = previous.Master;
            row.JobDirectory = string.IsNullOrWhiteSpace(previous.JobDirectory)
                ? row.JobDirectory
                : previous.JobDirectory;
            row.FinishedUtc = previous.FinishedUtc;
        }

        parsed.CreatedUtc = saved.CreatedUtc;
        return parsed;
    }

    public static void Save(string path, BatchState state)
    {
        FileSystemUtil.WriteAtomic(path, JobJson.Serialize(state));
    }

    public static BatchState? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JobJson.Deserialize<BatchState>(File.ReadAllText(path));
        }
        catch (Exception)
        {
            // A corrupt state file must not strand a batch; treat it as no progress recorded.
            return null;
        }
    }

    /// <summary>Splits on newlines while keeping newlines that sit inside a quoted cell.</summary>
    internal static List<string> SplitLines(string csv)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];

            if (character == '"')
            {
                // A doubled quote inside a quoted cell is an escaped quote, not a terminator.
                if (quoted && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    current.Append('"').Append('"');
                    index++;
                    continue;
                }

                quoted = !quoted;
                current.Append(character);
                continue;
            }

            if (!quoted && character is '\n')
            {
                lines.Add(current.ToString().TrimEnd('\r'));
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString().TrimEnd('\r'));
        }

        return lines;
    }

    /// <summary>Splits one CSV line into cells, honouring quotes and escaped quotes.</summary>
    internal static List<string> ParseRow(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];

            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                quoted = !quoted;
                continue;
            }

            if (character == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        cells.Add(current.ToString());
        return cells;
    }

    public static string ToMarkdown(BatchState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Batch");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- source: `{state.Source}`");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"- {state.Done} done, {state.Failed} failed, {state.Remaining} remaining of {state.Rows.Count}");
        builder.AppendLine();
        builder.AppendLine("| # | Status | Topic or script | Detail |");
        builder.AppendLine("|---|--------|-----------------|--------|");

        foreach (var row in state.Rows)
        {
            var what = string.IsNullOrWhiteSpace(row.Topic) ? Path.GetFileName(row.ScriptPath) : row.Topic;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Index} | {row.Status} | {TextUtil.Truncate(what, 46)} | {TextUtil.Truncate(row.Detail, 60)} |");
        }

        return builder.ToString();
    }
}
