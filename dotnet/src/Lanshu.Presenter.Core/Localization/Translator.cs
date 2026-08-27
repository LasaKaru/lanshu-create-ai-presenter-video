using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Localization;

public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}

public interface ITranslator
{
    string Provider { get; }

    /// <summary>
    /// Translates a batch of lines, returning exactly as many lines as it was given.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> lines,
        string targetLanguage,
        string sourceLanguage,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Picks whichever chat provider is configured. Translation reuses the script provider's
/// credentials rather than adding a second vendor to configure: anything good enough to write
/// the script is good enough to translate it.
/// </summary>
public sealed class TranslatorRouter
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public TranslatorRouter(AppSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    /// <summary>Returns null when nothing is configured, so callers can say so rather than guess.</summary>
    public ITranslator? Resolve()
    {
        var provider = _settings.Script.Provider?.Trim().ToLowerInvariant();

        if (provider is "anthropic" && !string.IsNullOrWhiteSpace(_store.GetSecret("ANTHROPIC_API_KEY")))
        {
            return new ChatTranslator(_settings, _store, _httpClient, ChatTranslator.Flavour.Anthropic);
        }

        if (provider is "openai" && !string.IsNullOrWhiteSpace(_store.GetSecret("OPENAI_API_KEY")))
        {
            return new ChatTranslator(_settings, _store, _httpClient, ChatTranslator.Flavour.OpenAi);
        }

        // Fall back to whichever key exists, since the user may have set a provider they have no
        // key for and a working translation is better than a silent skip.
        if (!string.IsNullOrWhiteSpace(_store.GetSecret("ANTHROPIC_API_KEY")))
        {
            return new ChatTranslator(_settings, _store, _httpClient, ChatTranslator.Flavour.Anthropic);
        }

        if (!string.IsNullOrWhiteSpace(_store.GetSecret("OPENAI_API_KEY")))
        {
            return new ChatTranslator(_settings, _store, _httpClient, ChatTranslator.Flavour.OpenAi);
        }

        return null;
    }
}

/// <summary>
/// Translates through a chat endpoint, one numbered line per caption.
///
/// The line count is the contract. A caption file's timings belong to the recording, not to the
/// words, so a translation that merges two lines into one or splits one into two silently
/// destroys the alignment. The prompt demands one output line per input line and the result is
/// checked; a mismatched reply is a failure, not something to paper over.
/// </summary>
public sealed class ChatTranslator : ITranslator
{
    public enum Flavour
    {
        Anthropic,
        OpenAi,
    }

    /// <summary>Small enough that a model keeps count reliably, large enough to keep context.</summary>
    private const int BatchSize = 40;

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;
    private readonly Flavour _flavour;

    public ChatTranslator(AppSettings settings, SettingsStore store, HttpClient httpClient, Flavour flavour)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
        _flavour = flavour;
    }

    public string Provider => _flavour == Flavour.Anthropic ? "anthropic" : "openai";

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> lines,
        string targetLanguage,
        string sourceLanguage,
        CancellationToken cancellationToken = default)
    {
        if (lines.Count == 0)
        {
            return Array.Empty<string>();
        }

        var translated = new List<string>(lines.Count);

        for (var offset = 0; offset < lines.Count; offset += BatchSize)
        {
            var batch = lines.Skip(offset).Take(BatchSize).ToList();
            var reply = await SendAsync(BuildPrompt(batch, targetLanguage, sourceLanguage), cancellationToken)
                .ConfigureAwait(false);

            var parsed = ParseNumbered(reply, batch.Count);
            if (parsed.Count != batch.Count)
            {
                throw new TranslationException(
                    $"the translator returned {parsed.Count} lines for a batch of {batch.Count}; "
                    + "caption timings belong to the recording, so the line count has to match exactly");
            }

            translated.AddRange(parsed);
        }

        return translated;
    }

    internal static string BuildPrompt(IReadOnlyList<string> lines, string targetLanguage, string sourceLanguage)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Translate these subtitle lines into {targetLanguage}.");
        if (!string.IsNullOrWhiteSpace(sourceLanguage) && !string.Equals(sourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"The source language is {sourceLanguage}.");
        }

        builder.AppendLine();
        builder.AppendLine("Rules:");
        builder.AppendLine("- Return exactly one translated line per numbered input line, same numbering.");
        builder.AppendLine("- Never merge two lines or split one; the timings belong to a recording that cannot change.");
        builder.AppendLine("- Keep each line about as long as the original so it still fits on screen.");
        builder.AppendLine("- Translate the meaning, not the words. Keep names, numbers and units as they are.");
        builder.AppendLine("- Output only the numbered lines, with no preamble and no commentary.");
        builder.AppendLine();

        for (var index = 0; index < lines.Count; index++)
        {
            builder.Append(index + 1).Append(". ").AppendLine(lines[index].Replace('\n', ' ').Trim());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads back the numbered reply. A model that adds a stray preamble is tolerated because the
    /// numbering identifies the real lines; anything else is left to the count check to reject.
    /// </summary>
    internal static IReadOnlyList<string> ParseNumbered(string reply, int expected)
    {
        var found = new string?[expected];

        foreach (var raw in (reply ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var dot = line.IndexOf('.');
            if (dot <= 0 || dot > 4)
            {
                continue;
            }

            if (!int.TryParse(line[..dot], out var number) || number < 1 || number > expected)
            {
                continue;
            }

            var text = line[(dot + 1)..].Trim();
            if (text.Length > 0)
            {
                found[number - 1] = text;
            }
        }

        return found.Where(line => line is not null).Select(line => line!).ToList();
    }

    private async Task<string> SendAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            return _flavour == Flavour.Anthropic
                ? await SendAnthropicAsync(prompt, cancellationToken).ConfigureAwait(false)
                : await SendOpenAiAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // "An error occurred while sending the request" names nothing an operator can fix.
            // The cause — refused connection, DNS, TLS — is always in the inner exception.
            var cause = exception.InnerException?.Message ?? exception.Message;
            throw new TranslationException($"could not reach the {Provider} endpoint: {cause}");
        }
    }

    private async Task<string> SendAnthropicAsync(string prompt, CancellationToken cancellationToken)
    {
        var apiKey = _store.GetSecret("ANTHROPIC_API_KEY");
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
                new JsonObject { ["role"] = "user", ["content"] = prompt },
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
            throw new TranslationException(
                $"Anthropic translation failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 300)}");
        }

        var text = new StringBuilder();
        if (JsonNode.Parse(body)?["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    text.Append(block["text"]?.GetValue<string>());
                }
            }
        }

        return text.ToString();
    }

    private async Task<string> SendOpenAiAsync(string prompt, CancellationToken cancellationToken)
    {
        var apiKey = _store.GetSecret("OPENAI_API_KEY");
        var baseUrl = string.IsNullOrWhiteSpace(_settings.Script.BaseUrl)
            ? "https://api.openai.com"
            : _settings.Script.BaseUrl.TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(_settings.Script.Model) ? "gpt-4o-mini" : _settings.Script.Model;

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt },
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
            throw new TranslationException(
                $"OpenAI translation failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 300)}");
        }

        return JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
    }
}
