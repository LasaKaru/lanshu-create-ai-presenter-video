using Lanshu.Presenter.Core.Configuration;

namespace Lanshu.Presenter.Core.Voice;

/// <summary>
/// Capability routing for voice generation. The acceptance priority from generation.md is
/// authorization first, then identity control, then clean audio — so an explicitly configured
/// provider wins, and the offline system voice is the last resort that always works.
/// </summary>
public sealed class SpeechRouter
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public SpeechRouter(AppSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public IReadOnlyList<ISpeechSynthesizer> All() => new ISpeechSynthesizer[]
    {
        new ElevenLabsSynthesizer(_settings.Voice, _store, _httpClient),
        new OpenAiSpeechSynthesizer(_settings.Voice, _store, _httpClient),
        new AzureSpeechSynthesizer(_settings.Voice, _store, _httpClient),
        new PiperSynthesizer(_settings.Voice),
        new SystemSpeechSynthesizer(),
        new MacSaySynthesizer(),
        new EspeakSynthesizer(),
    };

    public ISpeechSynthesizer? Create(string provider) => provider.Trim().ToLowerInvariant() switch
    {
        "elevenlabs" => new ElevenLabsSynthesizer(_settings.Voice, _store, _httpClient),
        "openai" => new OpenAiSpeechSynthesizer(_settings.Voice, _store, _httpClient),
        "azure" => new AzureSpeechSynthesizer(_settings.Voice, _store, _httpClient),
        "piper" => new PiperSynthesizer(_settings.Voice),
        "system" or "system-sapi" or "sapi" => new SystemSpeechSynthesizer(),
        "macos-say" or "say" => new MacSaySynthesizer(),
        "espeak" or "espeak-ng" => new EspeakSynthesizer(),
        _ => null,
    };

    public async Task<ISpeechSynthesizer> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var configured = (_settings.Voice.Provider ?? "auto").Trim().ToLowerInvariant();
        if (configured is not ("auto" or "" or "none"))
        {
            var chosen = Create(configured)
                ?? throw new SpeechSynthesisException($"unknown voice provider '{configured}'");
            if (!await chosen.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new SpeechSynthesisException(
                    $"the configured voice provider '{configured}' is not usable right now. Check its API key or local install in Settings.");
            }

            return chosen;
        }

        foreach (var candidate in All())
        {
            if (await candidate.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new SpeechSynthesisException(
            "No speech engine is available. On Windows the built-in system voices are used automatically; " +
            "elsewhere install espeak-ng or set an ElevenLabs, OpenAI, or Azure key in Settings.");
    }

    public async Task<IReadOnlyList<ISpeechSynthesizer>> AvailableAsync(CancellationToken cancellationToken = default)
    {
        var available = new List<ISpeechSynthesizer>();
        foreach (var candidate in All())
        {
            if (await candidate.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                available.Add(candidate);
            }
        }

        return available;
    }
}
