using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Voice;

public sealed class ElevenLabsSynthesizer : ISpeechSynthesizer
{
    private readonly VoiceSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public ElevenLabsSynthesizer(VoiceSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public string Provider => "elevenlabs";

    public string Model => string.IsNullOrWhiteSpace(_settings.Model) ? "eleven_multilingual_v2" : _settings.Model;

    public bool IsRemote => true;

    private string BaseUrl => string.IsNullOrWhiteSpace(_settings.BaseUrl)
        ? "https://api.elevenlabs.io"
        : _settings.BaseUrl.TrimEnd('/');

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.HasSecret("ELEVENLABS_API_KEY"));

    public async Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("ELEVENLABS_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<VoiceDescriptor>();
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/voices");
        message.Headers.Add("xi-api-key", apiKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var voices = new List<VoiceDescriptor>();
        if (JsonNode.Parse(body)?["voices"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                var id = entry?["voice_id"]?.GetValue<string>();
                var name = entry?["name"]?.GetValue<string>() ?? id;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    voices.Add(new VoiceDescriptor(id, name ?? id, string.Empty, Provider));
                }
            }
        }

        return voices;
    }

    public async Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("ELEVENLABS_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechSynthesisException("ELEVENLABS_API_KEY is not set");
        }

        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId) ? _settings.VoiceId : request.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new SpeechSynthesisException("an ElevenLabs voice id is required; pick one in Settings");
        }

        var payload = new JsonObject
        {
            ["text"] = request.Text,
            ["model_id"] = Model,
            ["voice_settings"] = new JsonObject
            {
                ["stability"] = 0.5,
                ["similarity_boost"] = 0.75,
            },
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=mp3_44100_128")
        {
            Content = JsonContent.Create(payload),
        };
        message.Headers.Add("xi-api-key", apiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await WriteAudioAsync(response, request.OutputPath, "ElevenLabs", cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteAudioAsync(
        HttpResponseMessage response,
        string outputPath,
        string providerName,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new SpeechSynthesisException(
                $"{providerName} synthesis failed ({(int)response.StatusCode}): {TextUtil.Truncate(error, 300)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await using (var target = File.Create(outputPath))
        {
            await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (FileSystemUtil.SafeLength(outputPath) == 0)
        {
            throw new SpeechSynthesisException($"{providerName} returned an empty audio file");
        }
    }
}

public sealed class OpenAiSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly VoiceSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public OpenAiSpeechSynthesizer(VoiceSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public string Provider => "openai";

    public string Model => string.IsNullOrWhiteSpace(_settings.Model) ? "gpt-4o-mini-tts" : _settings.Model;

    public bool IsRemote => true;

    private string BaseUrl => string.IsNullOrWhiteSpace(_settings.BaseUrl)
        ? "https://api.openai.com"
        : _settings.BaseUrl.TrimEnd('/');

    private static readonly string[] StockVoices =
        { "alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer" };

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.HasSecret("OPENAI_API_KEY"));

    public Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VoiceDescriptor> voices = StockVoices
            .Select(name => new VoiceDescriptor(name, TextUtil.TitleCase(name), string.Empty, Provider))
            .ToList();
        return Task.FromResult(voices);
    }

    public async Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechSynthesisException("OPENAI_API_KEY is not set");
        }

        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId) ? _settings.VoiceId : request.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            voiceId = "alloy";
        }

        var payload = new JsonObject
        {
            ["model"] = Model,
            ["voice"] = voiceId,
            ["input"] = request.Text,
            ["response_format"] = "mp3",
            ["speed"] = Math.Clamp(request.Rate, 0.25, 4.0),
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/audio/speech")
        {
            Content = JsonContent.Create(payload),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ElevenLabsSynthesizer
            .WriteAudioAsync(response, request.OutputPath, "OpenAI", cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class AzureSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly VoiceSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public AzureSpeechSynthesizer(VoiceSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public string Provider => "azure";

    public string Model => "azure-neural-tts";

    public bool IsRemote => true;

    private string Region => string.IsNullOrWhiteSpace(_settings.Region) ? "eastus" : _settings.Region.Trim();

    private string BaseUrl => string.IsNullOrWhiteSpace(_settings.BaseUrl)
        ? $"https://{Region}.tts.speech.microsoft.com"
        : _settings.BaseUrl.TrimEnd('/');

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.HasSecret("AZURE_SPEECH_KEY"));

    public async Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("AZURE_SPEECH_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<VoiceDescriptor>();
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/cognitiveservices/voices/list");
        message.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var voices = new List<VoiceDescriptor>();
        if (JsonNode.Parse(body) is JsonArray array)
        {
            foreach (var entry in array)
            {
                var id = entry?["ShortName"]?.GetValue<string>();
                var name = entry?["DisplayName"]?.GetValue<string>() ?? id;
                var locale = entry?["Locale"]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    voices.Add(new VoiceDescriptor(id, $"{name} ({locale})", locale, Provider));
                }
            }
        }

        return voices;
    }

    public async Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("AZURE_SPEECH_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SpeechSynthesisException("AZURE_SPEECH_KEY is not set");
        }

        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId) ? _settings.VoiceId : request.VoiceId;
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            voiceId = "en-US-AvaMultilingualNeural";
        }

        var locale = voiceId.Length >= 5 ? voiceId[..5] : "en-US";
        var ratePercent = (int)Math.Round((Math.Clamp(request.Rate, 0.5, 2.0) - 1.0) * 100);
        var ssml = $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{locale}">
              <voice name="{voiceId}">
                <prosody rate="{(ratePercent >= 0 ? "+" : string.Empty)}{ratePercent}%">{SecurityEscape(request.Text)}</prosody>
              </voice>
            </speak>
            """;

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/cognitiveservices/v1")
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
        };
        message.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
        message.Headers.Add("X-Microsoft-OutputFormat", "riff-48khz-16bit-mono-pcm");
        message.Headers.Add("User-Agent", "lanshu-presenter");

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        await ElevenLabsSynthesizer
            .WriteAudioAsync(response, request.OutputPath, "Azure Speech", cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SecurityEscape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
