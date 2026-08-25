using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Content;

public sealed record ScriptRequest
{
    public required string Topic { get; init; }

    public string Language { get; init; } = "auto";

    public string Audience { get; init; } = "general";

    public string Goal { get; init; } = "explain clearly";

    public double TargetSeconds { get; init; } = 60;

    public string Style { get; init; } = "credible contemporary presenter";

    public string Cta { get; init; } = string.Empty;
}

public interface IScriptWriter
{
    string Name { get; }

    Task<ScriptDocument> WriteAsync(ScriptRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Chooses the best available writer: a configured model when credentials exist, otherwise the
/// deterministic outline writer so the app still works completely offline.
/// </summary>
public sealed class ScriptWriterRouter
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public ScriptWriterRouter(AppSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public IScriptWriter Resolve()
    {
        var provider = (_settings.Script.Provider ?? "auto").Trim().ToLowerInvariant();
        return provider switch
        {
            "none" or "outline" => new OutlineScriptWriter(),
            "anthropic" => new AnthropicScriptWriter(_settings, _store, _httpClient),
            "openai" => new OpenAiScriptWriter(_settings, _store, _httpClient),
            _ => ResolveAuto(),
        };
    }

    private IScriptWriter ResolveAuto()
    {
        if (_store.HasSecret("ANTHROPIC_API_KEY"))
        {
            return new AnthropicScriptWriter(_settings, _store, _httpClient);
        }

        if (_store.HasSecret("OPENAI_API_KEY"))
        {
            return new OpenAiScriptWriter(_settings, _store, _httpClient);
        }

        return new OutlineScriptWriter();
    }
}

/// <summary>
/// Builds a usable spoken spine with no network call. It structures the presentation the way
/// generation.md prescribes and never invents facts about the topic — the wording stays
/// framing and transitions the presenter can fill in.
/// </summary>
public sealed class OutlineScriptWriter : IScriptWriter
{
    public string Name => "outline";

    public Task<ScriptDocument> WriteAsync(ScriptRequest request, CancellationToken cancellationToken = default)
    {
        var topic = request.Topic.Trim();
        var language = request.Language is "auto" or "" ? TextUtil.DetectLanguage(topic) : request.Language;
        var chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var beatCount = request.TargetSeconds switch
        {
            < 30 => 2,
            < 75 => 3,
            < 150 => 4,
            _ => 5,
        };

        var document = new ScriptDocument
        {
            Title = topic,
            Language = language,
            Source = "outline",
            Provider = "built-in",
        };

        document.Notes.Add(
            "Written by the built-in outline writer. Add an Anthropic or OpenAI key in Settings for a fully drafted script, or edit the narration before locking audio.");

        if (chinese)
        {
            document.Beats.Add(new ScriptBeat
            {
                Role = "hook",
                Title = "开场",
                Keyword = "重点",
                Narration = $"{topic}，很多人第一次接触时都会卡住。这条视频用不到一分钟把它讲清楚。",
                VisualNote = "presenter close, keyword burst on the free side",
            });

            for (var index = 1; index <= beatCount; index++)
            {
                document.Beats.Add(new ScriptBeat
                {
                    Role = "beat",
                    Title = $"要点 {index}",
                    Keyword = $"要点{index}",
                    Narration = $"第{index}点。这里说明{topic}在这一步的关键动作，以及做对之后会得到什么结果。",
                    VisualNote = "one supporting visual or a clean keyword card",
                });
            }

            document.Beats.Add(new ScriptBeat
            {
                Role = "close",
                Title = "收尾",
                Keyword = "记住这点",
                Narration = string.IsNullOrWhiteSpace(request.Cta)
                    ? $"把这几点串起来，{topic}就不再难。"
                    : $"把这几点串起来，{topic}就不再难。{request.Cta}",
                VisualNote = "settled presenter, closing card",
            });
        }
        else
        {
            document.Beats.Add(new ScriptBeat
            {
                Role = "hook",
                Title = "Hook",
                Keyword = "the point",
                Narration = $"Most explanations of {topic} start in the wrong place. Here is the version that actually lands, in under a minute.",
                VisualNote = "presenter close, keyword burst on the free side",
            });

            for (var index = 1; index <= beatCount; index++)
            {
                document.Beats.Add(new ScriptBeat
                {
                    Role = "beat",
                    Title = $"Beat {index}",
                    Keyword = $"step {index}",
                    Narration = $"Step {index}. Describe the specific move that {topic} requires here, and what changes once it is done correctly.",
                    VisualNote = "one supporting visual or a clean keyword card",
                });
            }

            document.Beats.Add(new ScriptBeat
            {
                Role = "close",
                Title = "Close",
                Keyword = "remember this",
                Narration = string.IsNullOrWhiteSpace(request.Cta)
                    ? $"Put those together and {topic} stops being confusing."
                    : $"Put those together and {topic} stops being confusing. {request.Cta}",
                VisualNote = "settled presenter, closing card",
            });
        }

        return Task.FromResult(document);
    }
}

internal static class ScriptPrompt
{
    public static string Build(ScriptRequest request)
    {
        var language = request.Language is "auto" or ""
            ? "the same language as the topic"
            : request.Language;

        var beats = request.TargetSeconds switch
        {
            < 30 => "2",
            < 75 => "2 to 3",
            < 150 => "3 to 4",
            _ => "4 to 5",
        };

        var builder = new StringBuilder();
        builder.AppendLine("You write narration for a single-presenter explainer video.");
        builder.AppendLine();
        builder.AppendLine($"Topic: {request.Topic}");
        builder.AppendLine($"Audience: {request.Audience}");
        builder.AppendLine($"Goal: {request.Goal}");
        builder.AppendLine($"Target spoken duration: about {request.TargetSeconds:0} seconds");
        builder.AppendLine($"Language: {language}");
        builder.AppendLine($"Tone: {request.Style}");
        if (!string.IsNullOrWhiteSpace(request.Cta))
        {
            builder.AppendLine($"Requested closing action: {request.Cta}");
        }

        builder.AppendLine();
        builder.AppendLine("Structure: hook, promise, " + beats + " useful beats, synthesis, close.");
        builder.AppendLine("Rules:");
        builder.AppendLine("- Write words a person actually says. No stage directions, headings, emoji, or markdown inside narration.");
        builder.AppendLine("- Short sentences a presenter can breathe through.");
        builder.AppendLine("- Do not invent statistics, prices, dates, product claims, or quotes. If a number matters, describe it qualitatively.");
        builder.AppendLine("- Give each beat one keyword of one to three words, suitable for an on-screen callout.");
        builder.AppendLine("- Do not add promotional copy unless a closing action was requested.");
        builder.AppendLine();
        builder.AppendLine("Reply with JSON only, no prose and no code fence, in this shape:");
        builder.AppendLine("""
        {
          "title": "short video title",
          "language": "ISO code such as en or zh",
          "beats": [
            {"role": "hook|beat|synthesis|close", "title": "short label", "narration": "spoken words", "keyword": "callout", "visual_note": "one visual idea"}
          ],
          "pronunciations": ["term -> how to say it"],
          "notes": ["anything the operator should verify"]
        }
        """);

        return builder.ToString();
    }

    public static ScriptDocument Parse(string content, ScriptRequest request, string provider, string model)
    {
        var json = ExtractJson(content)
            ?? throw new InvalidDataException("the script model did not return JSON");

        var node = JsonNode.Parse(json)
            ?? throw new InvalidDataException("the script model returned empty JSON");

        var document = new ScriptDocument
        {
            Title = node["title"]?.GetValue<string>()?.Trim() ?? request.Topic,
            Language = node["language"]?.GetValue<string>()?.Trim() ?? TextUtil.DetectLanguage(request.Topic),
            Source = "model",
            Provider = provider,
            Model = model,
        };

        if (node["beats"] is JsonArray beats)
        {
            foreach (var entry in beats)
            {
                if (entry is null)
                {
                    continue;
                }

                var narration = TextUtil.ToSpokenText(entry["narration"]?.GetValue<string>() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(narration))
                {
                    continue;
                }

                document.Beats.Add(new ScriptBeat
                {
                    Role = (entry["role"]?.GetValue<string>() ?? "beat").Trim().ToLowerInvariant(),
                    Title = entry["title"]?.GetValue<string>()?.Trim() ?? string.Empty,
                    Narration = narration,
                    Keyword = entry["keyword"]?.GetValue<string>()?.Trim() ?? string.Empty,
                    VisualNote = entry["visual_note"]?.GetValue<string>()?.Trim() ?? string.Empty,
                });
            }
        }

        if (document.Beats.Count == 0)
        {
            throw new InvalidDataException("the script model returned no usable beats");
        }

        foreach (var value in ReadStrings(node["pronunciations"]))
        {
            document.Pronunciations.Add(value);
        }

        foreach (var value in ReadStrings(node["notes"]))
        {
            document.Notes.Add(value);
        }

        return document;
    }

    private static IEnumerable<string> ReadStrings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            yield break;
        }

        foreach (var entry in array)
        {
            var value = entry?.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>Models sometimes wrap JSON in prose or a fence; take the outermost object.</summary>
    internal static string? ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return content.Substring(start, end - start + 1);
    }
}

public sealed class AnthropicScriptWriter : IScriptWriter
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public AnthropicScriptWriter(AppSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public string Name => "anthropic";

    public async Task<ScriptDocument> WriteAsync(ScriptRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("ANTHROPIC_API_KEY is not set");
        }

        var baseUrl = string.IsNullOrWhiteSpace(_settings.Script.BaseUrl)
            ? "https://api.anthropic.com"
            : _settings.Script.BaseUrl.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(_settings.Script.Model) ? "claude-opus-5" : _settings.Script.Model;

        var payload = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = _settings.Script.MaxOutputTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = ScriptPrompt.Build(request),
                },
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = JsonContent.Create(payload),
        };
        message.Headers.Add("x-api-key", apiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Anthropic request failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 400)}");
        }

        var node = JsonNode.Parse(body);
        var text = new StringBuilder();
        if (node?["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    text.Append(block["text"]?.GetValue<string>());
                }
            }
        }

        return ScriptPrompt.Parse(text.ToString(), request, "anthropic", model);
    }
}

public sealed class OpenAiScriptWriter : IScriptWriter
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public OpenAiScriptWriter(AppSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public string Name => "openai";

    public async Task<ScriptDocument> WriteAsync(ScriptRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not set");
        }

        var baseUrl = string.IsNullOrWhiteSpace(_settings.Script.BaseUrl)
            ? "https://api.openai.com"
            : _settings.Script.BaseUrl.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(_settings.Script.Model) || _settings.Script.Model.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            ? "gpt-4o-mini"
            : _settings.Script.Model;

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = ScriptPrompt.Build(request),
                },
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        message.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenAI request failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 400)}");
        }

        var content = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                      ?? string.Empty;
        return ScriptPrompt.Parse(content, request, "openai", model);
    }
}
