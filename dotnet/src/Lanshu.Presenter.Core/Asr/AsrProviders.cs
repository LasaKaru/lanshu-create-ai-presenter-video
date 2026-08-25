using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Asr;

public sealed class OpenAiWhisperAsr : IAsrProvider
{
    private readonly AsrSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public OpenAiWhisperAsr(AsrSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    public string Provider => "openai-whisper";

    public string Model => string.IsNullOrWhiteSpace(_settings.Model) ? "whisper-1" : _settings.Model;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_store.HasSecret("OPENAI_API_KEY"));

    public async Task<AsrResult> TranscribeAsync(
        string audioPath,
        string language,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _store.GetSecret("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is not set");
        }

        var baseUrl = string.IsNullOrWhiteSpace(_settings.BaseUrl)
            ? "https://api.openai.com"
            : _settings.BaseUrl.TrimEnd('/');

        using var content = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(audioPath));
        content.Add(new StringContent(Model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        content.Add(new StringContent("word"), "timestamp_granularities[]");
        content.Add(new StringContent("segment"), "timestamp_granularities[]");
        if (!string.IsNullOrWhiteSpace(language) && language != "auto")
        {
            content.Add(new StringContent(language[..Math.Min(2, language.Length)]), "language");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/audio/transcriptions")
        {
            Content = content,
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"transcription failed ({(int)response.StatusCode}): {TextUtil.Truncate(body, 300)}");
        }

        var node = JsonNode.Parse(body);
        var result = new AsrResult
        {
            Provider = Provider,
            Model = Model,
            Text = node?["text"]?.GetValue<string>() ?? string.Empty,
            WordTimingsAreMeasured = true,
        };

        if (node?["words"] is JsonArray words)
        {
            foreach (var entry in words)
            {
                var word = entry?["word"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(word))
                {
                    continue;
                }

                result.Words.Add(new WordTiming
                {
                    Word = word.Trim(),
                    StartSeconds = ReadDouble(entry, "start"),
                    EndSeconds = ReadDouble(entry, "end"),
                });
            }
        }

        if (result.Words.Count == 0 && node?["segments"] is JsonArray segments)
        {
            // Fall back to segment granularity when the endpoint does not return word timings.
            foreach (var entry in segments)
            {
                var text = entry?["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var start = ReadDouble(entry, "start");
                var end = ReadDouble(entry, "end");
                foreach (var timing in ProportionalAligner.AlignSegment(text.Trim(), start, Math.Max(0.05, end - start)))
                {
                    result.Words.Add(timing);
                }
            }

            result.WordTimingsAreMeasured = false;
        }

        return result;
    }

    private static double ReadDouble(JsonNode? node, string key)
    {
        var value = node?[key]?.ToJsonString();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}

/// <summary>
/// whisper.cpp on the local machine. Runs fully offline once a model file is downloaded.
/// </summary>
public sealed class WhisperCliAsr : IAsrProvider
{
    private readonly AsrSettings _settings;

    public WhisperCliAsr(AsrSettings settings)
    {
        _settings = settings;
    }

    public string Provider => "whisper-cli";

    public string Model => Path.GetFileNameWithoutExtension(_settings.WhisperModelPath);

    private string? ExecutablePath => ToolLocator.Find("whisper-cli", _settings.WhisperCliPath)
                                      ?? ToolLocator.Find("main", _settings.WhisperCliPath)
                                      ?? ToolLocator.Find("whisper");

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ExecutablePath is not null
                        && !string.IsNullOrWhiteSpace(_settings.WhisperModelPath)
                        && File.Exists(FileSystemUtil.ExpandPath(_settings.WhisperModelPath)));

    public async Task<AsrResult> TranscribeAsync(
        string audioPath,
        string language,
        CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath
            ?? throw new ExternalToolException("whisper-cli is not installed or not on PATH");
        var model = FileSystemUtil.ExpandPath(_settings.WhisperModelPath);

        var workDirectory = Path.Combine(Path.GetTempPath(), $"lanshu-asr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var outputStem = Path.Combine(workDirectory, "transcript");

        try
        {
            var arguments = new List<string>
            {
                "-m", model,
                "-f", audioPath,
                "-oj",
                "-of", outputStem,
                "--max-len", "1",
                "-ml", "1",
            };

            if (!string.IsNullOrWhiteSpace(language) && language != "auto")
            {
                arguments.AddRange(new[] { "-l", language[..Math.Min(2, language.Length)] });
            }

            var result = await ProcessRunner
                .RunAsync(executable, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            result.EnsureSuccess("whisper transcription");

            var jsonPath = outputStem + ".json";
            if (!File.Exists(jsonPath))
            {
                throw new ExternalToolException("whisper did not write a JSON transcript");
            }

            return Parse(await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(workDirectory);
        }
    }

    internal AsrResult Parse(string json)
    {
        var node = JsonNode.Parse(json);
        var result = new AsrResult
        {
            Provider = Provider,
            Model = Model,
            WordTimingsAreMeasured = true,
        };

        if (node?["transcription"] is JsonArray entries)
        {
            foreach (var entry in entries)
            {
                var text = entry?["text"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                // whisper.cpp reports offsets in milliseconds.
                var from = entry?["offsets"]?["from"]?.GetValue<double>() ?? 0;
                var to = entry?["offsets"]?["to"]?.GetValue<double>() ?? from;
                result.Words.Add(new WordTiming
                {
                    Word = text,
                    StartSeconds = Math.Round(from / 1000.0, 3),
                    EndSeconds = Math.Round(to / 1000.0, 3),
                });
            }
        }

        result.Text = string.Join(" ", result.Words.Select(word => word.Word));
        return result;
    }
}

public sealed class AsrRouter
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public AsrRouter(AppSettings settings, SettingsStore store, HttpClient httpClient)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
    }

    /// <summary>Returns null when no transcription service is configured; the aligner covers that case.</summary>
    public async Task<IAsrProvider?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var configured = (_settings.Asr.Provider ?? "auto").Trim().ToLowerInvariant();
        if (configured is "none" or "aligner")
        {
            return null;
        }

        IAsrProvider[] candidates = configured switch
        {
            "openai" => new IAsrProvider[] { new OpenAiWhisperAsr(_settings.Asr, _store, _httpClient) },
            "whisper-cli" => new IAsrProvider[] { new WhisperCliAsr(_settings.Asr) },
            _ => new IAsrProvider[]
            {
                new WhisperCliAsr(_settings.Asr),
                new OpenAiWhisperAsr(_settings.Asr, _store, _httpClient),
            },
        };

        foreach (var candidate in candidates)
        {
            if (await candidate.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }
}
