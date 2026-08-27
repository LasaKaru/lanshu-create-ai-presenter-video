using System.Text;
using System.Text.Json.Serialization;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Content;

public enum BeatRole
{
    Hook,
    Beat,
    Synthesis,
    Close,
}

public sealed class ScriptBeat
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "beat";

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("narration")]
    public string Narration { get; set; } = string.Empty;

    [JsonPropertyName("keyword")]
    public string Keyword { get; set; } = string.Empty;

    [JsonPropertyName("visual_note")]
    public string VisualNote { get; set; } = string.Empty;

    [JsonIgnore]
    public BeatRole RoleValue => Role.ToLowerInvariant() switch
    {
        "hook" => BeatRole.Hook,
        "synthesis" => BeatRole.Synthesis,
        "close" => BeatRole.Close,
        _ => BeatRole.Beat,
    };
}

public sealed class ScriptDocument
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "outline";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("beats")]
    public List<ScriptBeat> Beats { get; set; } = new();

    [JsonPropertyName("pronunciations")]
    public List<string> Pronunciations { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();

    [JsonIgnore]
    public string Narration => string.Join(
        "\n",
        Beats.Select(beat => beat.Narration.Trim()).Where(text => text.Length > 0));

    /// <summary>
    /// A deep copy, so a translated version can be edited without touching the original —
    /// the delivered English video is still built from the document this was cloned from.
    /// </summary>
    public ScriptDocument Clone() => new()
    {
        Title = Title,
        Language = Language,
        Source = Source,
        Provider = Provider,
        Model = Model,
        Pronunciations = new List<string>(Pronunciations),
        Notes = new List<string>(Notes),
        Beats = Beats.Select(beat => new ScriptBeat
        {
            Role = beat.Role,
            Title = beat.Title,
            Narration = beat.Narration,
            Keyword = beat.Keyword,
            VisualNote = beat.VisualNote,
        }).ToList(),
    };

    [JsonIgnore]
    public double EstimatedSeconds => TextUtil.EstimateSpokenSeconds(Narration);

    public string ToScriptMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {Title}");
        builder.AppendLine();
        builder.AppendLine($"- language: `{Language}`");
        builder.AppendLine($"- source: `{Source}`");
        if (!string.IsNullOrWhiteSpace(Provider))
        {
            builder.AppendLine($"- writer: `{Provider}{(string.IsNullOrWhiteSpace(Model) ? string.Empty : " / " + Model)}`");
        }

        builder.AppendLine($"- estimated narration: `{EstimatedSeconds:0.0}s`");
        builder.AppendLine();
        builder.AppendLine("## Narration");
        builder.AppendLine();

        foreach (var beat in Beats)
        {
            builder.AppendLine($"### {beat.Role.ToUpperInvariant()} — {beat.Title}");
            builder.AppendLine();
            builder.AppendLine(beat.Narration.Trim());
            builder.AppendLine();
        }

        if (Pronunciations.Count > 0)
        {
            builder.AppendLine("## Pronunciations");
            builder.AppendLine();
            foreach (var entry in Pronunciations)
            {
                builder.AppendLine($"- {entry}");
            }

            builder.AppendLine();
        }

        if (Notes.Count > 0)
        {
            builder.AppendLine("## Notes");
            builder.AppendLine();
            foreach (var note in Notes)
            {
                builder.AppendLine($"- {note}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public string ToBeatSheetMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Beat sheet — {Title}");
        builder.AppendLine();
        builder.AppendLine("| # | Role | Title | Keyword | Estimated | Visual |");
        builder.AppendLine("|---|------|-------|---------|-----------|--------|");

        var index = 0;
        foreach (var beat in Beats)
        {
            index++;
            var seconds = TextUtil.EstimateSpokenSeconds(beat.Narration);
            builder.AppendLine(
                $"| {index} | {beat.Role} | {Escape(beat.Title)} | {Escape(beat.Keyword)} | {seconds:0.0}s | {Escape(beat.VisualNote)} |");
        }

        builder.AppendLine();
        builder.AppendLine($"Total estimated narration: **{EstimatedSeconds:0.0}s**");
        builder.AppendLine();
        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|");

    /// <summary>
    /// Reads a supplied script file. Headings become beat titles; everything else is narration.
    /// Factual meaning is preserved — nothing is rewritten here.
    /// </summary>
    public static ScriptDocument FromSuppliedText(string markdown, string fallbackTitle)
    {
        // The title stays empty until the parse finishes so a leading "# Heading" in the script
        // wins over the caller's fallback rather than being skipped by it.
        var document = new ScriptDocument { Source = "supplied" };

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var currentTitle = string.Empty;
        var buffer = new StringBuilder();
        var beats = new List<(string Title, string Body)>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(document.Title))
            {
                document.Title = line[2..].Trim();
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                if (buffer.Length > 0)
                {
                    beats.Add((currentTitle, buffer.ToString()));
                    buffer.Clear();
                }

                currentTitle = line.TrimStart('#').Trim();
                continue;
            }

            buffer.AppendLine(line);
        }

        if (buffer.Length > 0)
        {
            beats.Add((currentTitle, buffer.ToString()));
        }

        var kept = beats
            .Select(entry => (entry.Title, Body: TextUtil.ToSpokenText(entry.Body)))
            .Where(entry => entry.Body.Length > 0)
            .ToList();

        if (kept.Count == 0)
        {
            var spoken = TextUtil.ToSpokenText(markdown);
            if (spoken.Length > 0)
            {
                kept.Add((string.Empty, spoken));
            }
        }

        // A single wall of text still needs beats for chapters and callouts; split by sentence groups.
        if (kept.Count == 1)
        {
            var sentences = TextUtil.SplitSentences(kept[0].Body);
            if (sentences.Count >= 4)
            {
                kept = ChunkSentences(sentences);
            }
        }

        for (var index = 0; index < kept.Count; index++)
        {
            var role = index == 0
                ? "hook"
                : index == kept.Count - 1 && kept.Count > 2
                    ? "close"
                    : "beat";

            document.Beats.Add(new ScriptBeat
            {
                Role = role,
                Title = string.IsNullOrWhiteSpace(kept[index].Title)
                    ? $"Section {index + 1}"
                    : kept[index].Title,
                Narration = kept[index].Body.Trim(),
            });
        }

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            document.Title = !string.IsNullOrWhiteSpace(fallbackTitle)
                ? fallbackTitle
                : TextUtil.Truncate(document.Beats.FirstOrDefault()?.Narration ?? "Presenter video", 60);
        }

        document.Language = TextUtil.DetectLanguage(document.Narration);
        document.EnsureKeywords();
        return document;
    }

    /// <summary>
    /// Gives every beat a callout keyword. A supplied script rarely carries one, and the keyword
    /// presets need a real spoken word to anchor to — so the term is picked from the beat's own
    /// narration and never repeats within the video.
    /// </summary>
    public void EnsureKeywords()
    {
        var used = new HashSet<string>(
            Beats.Where(beat => !string.IsNullOrWhiteSpace(beat.Keyword)).Select(beat => beat.Keyword.ToLowerInvariant()),
            StringComparer.Ordinal);

        foreach (var beat in Beats)
        {
            if (!string.IsNullOrWhiteSpace(beat.Keyword))
            {
                continue;
            }

            var keyword = PickKeyword(beat.Narration, used);
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            beat.Keyword = keyword;
            used.Add(keyword.ToLowerInvariant());
        }
    }

    private static string PickKeyword(string narration, ISet<string> used)
    {
        var tokens = TextUtil.Tokenize(narration);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        if (TextUtil.IsCjk(narration))
        {
            // Prefer a two-character run that has not been used as a callout yet.
            for (var index = 0; index + 1 < tokens.Count; index++)
            {
                var candidate = tokens[index] + tokens[index + 1];
                if (!used.Contains(candidate.ToLowerInvariant()))
                {
                    return candidate;
                }
            }

            return tokens[0];
        }

        var ranked = tokens
            .Where(token => token.Length >= 5 && !KeywordStopWords.Contains(token.ToLowerInvariant()))
            .Where(token => !used.Contains(token.ToLowerInvariant()))
            .GroupBy(token => token.ToLowerInvariant())
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .Select(group => group.First())
            .FirstOrDefault();

        return ranked ?? string.Empty;
    }

    private static readonly HashSet<string> KeywordStopWords = new(StringComparer.Ordinal)
    {
        "about", "after", "again", "against", "because", "before", "being", "between", "could",
        "every", "first", "going", "might", "other", "should", "their", "there", "these", "thing",
        "things", "think", "those", "through", "under", "until", "where", "which", "while",
        "would", "yours", "really", "actually", "something", "someone", "everything",
    };

    private static List<(string Title, string Body)> ChunkSentences(IReadOnlyList<string> sentences)
    {
        var target = Math.Clamp((int)Math.Ceiling(sentences.Count / 4.0), 1, 6);
        var chunks = new List<(string, string)>();
        for (var index = 0; index < sentences.Count; index += target)
        {
            var slice = sentences.Skip(index).Take(target);
            chunks.Add((string.Empty, string.Join(" ", slice)));
        }

        return chunks;
    }
}
