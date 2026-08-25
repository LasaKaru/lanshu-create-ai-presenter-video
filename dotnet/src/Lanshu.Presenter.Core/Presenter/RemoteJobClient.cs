using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Presenter;

public sealed record RemoteJobInputs
{
    public string ImagePath { get; init; } = string.Empty;

    public string AudioPath { get; init; } = string.Empty;

    public string VideoPath { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public string NegativePrompt { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    public int Fps { get; init; }

    public double DurationSeconds { get; init; }
}

public sealed record RemoteJobOutcome(string TaskId, string ResultUrl, string SanitizedRequest, string LastStatusBody);

/// <summary>
/// Drives any submit-poll-download video API described by <see cref="RemoteJobSettings"/>.
/// The request body is a user-supplied JSON template, so no vendor is hard-coded, and the
/// archived copy of the request always has credentials and data URIs stripped out.
/// </summary>
public sealed class RemoteJobClient
{
    private readonly RemoteJobSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;

    public RemoteJobClient(
        RemoteJobSettings settings,
        SettingsStore store,
        HttpClient httpClient,
        Action<string>? log = null)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
        _log = log;
    }

    public async Task<RemoteJobOutcome> RunAsync(
        RemoteJobInputs inputs,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            throw new PresenterGenerationException(
                "the remote provider is not fully configured; set its submit URL and request template in Settings");
        }

        var body = await RenderTemplateAsync(inputs, cancellationToken).ConfigureAwait(false);
        var sanitized = Sanitize(body);

        _log?.Invoke($"Submitting remote job to {_settings.SubmitUrl}");
        using var submitMessage = new HttpRequestMessage(HttpMethod.Post, _settings.SubmitUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyAuth(submitMessage);

        using var submitResponse = await _httpClient.SendAsync(submitMessage, cancellationToken).ConfigureAwait(false);
        var submitBody = await submitResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!submitResponse.IsSuccessStatusCode)
        {
            throw new PresenterGenerationException(
                $"remote submit failed ({(int)submitResponse.StatusCode}): {TextUtil.Truncate(submitBody, 400)}");
        }

        var submitNode = JsonNode.Parse(submitBody);
        var taskId = ReadPath(submitNode, _settings.TaskIdPath) ?? string.Empty;

        // Some endpoints return the finished asset immediately; only poll when they do not.
        var immediateResult = ReadPath(submitNode, _settings.ResultUrlPath);
        var statusBody = submitBody;

        if (string.IsNullOrWhiteSpace(immediateResult))
        {
            if (string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(_settings.StatusUrlTemplate))
            {
                throw new PresenterGenerationException(
                    "the remote response contained neither a result URL nor a task id to poll");
            }

            _log?.Invoke($"Remote task accepted: {(string.IsNullOrWhiteSpace(taskId) ? "(no id)" : taskId)}");
            (immediateResult, statusBody) = await PollAsync(taskId, cancellationToken).ConfigureAwait(false);
        }

        await DownloadAsync(immediateResult!, destinationPath, cancellationToken).ConfigureAwait(false);
        return new RemoteJobOutcome(taskId, immediateResult!, sanitized, statusBody);
    }

    /// <summary>
    /// Polls an already-submitted task. Used on resume so an interrupted run never pays twice
    /// for the same generation.
    /// </summary>
    public async Task<(string? ResultUrl, string Body)> PollAsync(
        string taskId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.StatusUrlTemplate))
        {
            throw new PresenterGenerationException("no status URL template is configured for polling");
        }

        var statusUrl = _settings.StatusUrlTemplate
            .Replace("{{TASK_ID}}", Uri.EscapeDataString(taskId), StringComparison.Ordinal)
            .Replace("{task_id}", Uri.EscapeDataString(taskId), StringComparison.Ordinal);

        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollIntervalSeconds, 1, 120));
        var deadline = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 30, 21600));
        var lastBody = string.Empty;

        while (deadline.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var message = new HttpRequestMessage(HttpMethod.Get, statusUrl);
            ApplyAuth(message);
            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            lastBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new PresenterGenerationException(
                    $"remote status check failed ({(int)response.StatusCode}): {TextUtil.Truncate(lastBody, 300)}");
            }

            var node = JsonNode.Parse(lastBody);
            var status = (ReadPath(node, _settings.StatusPath) ?? string.Empty).Trim().ToLowerInvariant();
            var resultUrl = ReadPath(node, _settings.ResultUrlPath);

            if (!string.IsNullOrWhiteSpace(resultUrl)
                && (_settings.SucceededValues.Count == 0
                    || _settings.SucceededValues.Any(value => string.Equals(value, status, StringComparison.OrdinalIgnoreCase))
                    || string.IsNullOrEmpty(status)))
            {
                return (resultUrl, lastBody);
            }

            if (_settings.FailedValues.Any(value => string.Equals(value, status, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PresenterGenerationException(
                    $"the remote task reported '{status}': {TextUtil.Truncate(lastBody, 400)}");
            }

            _log?.Invoke($"Remote task status: {(string.IsNullOrEmpty(status) ? "pending" : status)} ({deadline.Elapsed.TotalSeconds:0}s)");
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        throw new PresenterGenerationException(
            $"the remote task did not finish within {timeout.TotalMinutes:0} minutes. Its id is recorded in the job so it can be polled again instead of resubmitted.");
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        _log?.Invoke("Downloading remote result");
        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PresenterGenerationException(
                $"could not download the remote result ({(int)response.StatusCode})");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await using (var target = File.Create(destination))
        {
            await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        if (FileSystemUtil.SafeLength(destination) == 0)
        {
            throw new PresenterGenerationException("the remote result downloaded as an empty file");
        }
    }

    private void ApplyAuth(HttpRequestMessage message)
    {
        var key = _store.GetSecret(_settings.SecretKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var header = string.IsNullOrWhiteSpace(_settings.AuthHeader) ? "Authorization" : _settings.AuthHeader;
        if (string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            message.Headers.Authorization = string.IsNullOrWhiteSpace(_settings.AuthScheme)
                ? new AuthenticationHeaderValue(key)
                : new AuthenticationHeaderValue(_settings.AuthScheme, key);
            return;
        }

        message.Headers.TryAddWithoutValidation(header, key);
    }

    private async Task<string> RenderTemplateAsync(RemoteJobInputs inputs, CancellationToken cancellationToken)
    {
        var template = _settings.RequestTemplate;
        var body = template;

        if (template.Contains("{{IMAGE_DATA_URI}}", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(inputs.ImagePath))
        {
            body = body.Replace(
                "{{IMAGE_DATA_URI}}",
                await ToDataUriAsync(inputs.ImagePath, cancellationToken).ConfigureAwait(false),
                StringComparison.Ordinal);
        }

        if (template.Contains("{{AUDIO_DATA_URI}}", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(inputs.AudioPath))
        {
            body = body.Replace(
                "{{AUDIO_DATA_URI}}",
                await ToDataUriAsync(inputs.AudioPath, cancellationToken).ConfigureAwait(false),
                StringComparison.Ordinal);
        }

        if (template.Contains("{{VIDEO_DATA_URI}}", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(inputs.VideoPath))
        {
            body = body.Replace(
                "{{VIDEO_DATA_URI}}",
                await ToDataUriAsync(inputs.VideoPath, cancellationToken).ConfigureAwait(false),
                StringComparison.Ordinal);
        }

        body = body
            .Replace("{{IMAGE_BASE64}}", await ToBase64Async(inputs.ImagePath, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal)
            .Replace("{{AUDIO_BASE64}}", await ToBase64Async(inputs.AudioPath, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal)
            .Replace("{{VIDEO_BASE64}}", await ToBase64Async(inputs.VideoPath, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal)
            .Replace("{{PROMPT}}", JsonEscape(inputs.Prompt), StringComparison.Ordinal)
            .Replace("{{NEGATIVE_PROMPT}}", JsonEscape(inputs.NegativePrompt), StringComparison.Ordinal)
            .Replace("{{WIDTH}}", inputs.Width.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{HEIGHT}}", inputs.Height.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{FPS}}", inputs.Fps.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{DURATION}}", inputs.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{DURATION_INT}}", ((int)Math.Ceiling(inputs.DurationSeconds)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{MODEL}}", JsonEscape(_settings.Model), StringComparison.Ordinal)
            .Replace("{{VERSION}}", JsonEscape(_settings.Version), StringComparison.Ordinal);

        // Fail loudly on a malformed template rather than sending nonsense to a billable endpoint.
        try
        {
            using var _ = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new PresenterGenerationException(
                $"the request template did not produce valid JSON: {exception.Message}");
        }

        return body;
    }

    private static async Task<string> ToDataUriAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return $"data:{MimeFor(path)};base64,{Convert.ToBase64String(bytes)}";
    }

    private static async Task<string> ToBase64Async(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        return Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }

    internal static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };

    internal static string Quote(string value) => JsonSerializer.Serialize(value ?? string.Empty);

    internal static string JsonEscape(string value)
    {
        var encoded = JsonSerializer.Serialize(value ?? string.Empty);
        return encoded[1..^1];
    }

    /// <summary>
    /// Produces an archivable copy of the request: credentials are never in the body, and
    /// embedded media is replaced with a short marker so the record stays reviewable and small.
    /// </summary>
    internal static string Sanitize(string body)
    {
        var node = JsonNode.Parse(body);
        if (node is null)
        {
            return "{}";
        }

        Walk(node);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        static void Walk(JsonNode current)
        {
            switch (current)
            {
                case JsonObject jsonObject:
                    foreach (var key in jsonObject.Select(pair => pair.Key).ToList())
                    {
                        var child = jsonObject[key];
                        if (child is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            jsonObject[key] = Redact(key, text);
                        }
                        else if (child is not null)
                        {
                            Walk(child);
                        }
                    }

                    break;

                case JsonArray array:
                    for (var index = 0; index < array.Count; index++)
                    {
                        var child = array[index];
                        if (child is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            array[index] = Redact(string.Empty, text);
                        }
                        else if (child is not null)
                        {
                            Walk(child);
                        }
                    }

                    break;
            }
        }

        static string Redact(string key, string text)
        {
            if (text.StartsWith("data:", StringComparison.Ordinal))
            {
                var marker = text.IndexOf(';', StringComparison.Ordinal);
                var kind = marker > 5 ? text[5..marker] : "binary";
                return $"<{kind} omitted, {text.Length} chars>";
            }

            if (text.Length > 512)
            {
                return $"<{text.Length} chars omitted>";
            }

            var lowered = key.ToLowerInvariant();
            if (lowered.Contains("key", StringComparison.Ordinal)
                || lowered.Contains("token", StringComparison.Ordinal)
                || lowered.Contains("secret", StringComparison.Ordinal)
                || lowered.Contains("authorization", StringComparison.Ordinal))
            {
                return "<redacted>";
            }

            // Signed URLs expire and can leak access; keep only the origin and path.
            if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase) && text.Contains('?', StringComparison.Ordinal))
            {
                return text[..text.IndexOf('?', StringComparison.Ordinal)] + "?<query omitted>";
            }

            return text;
        }
    }

    /// <summary>Reads a dotted path such as "output.video_url" or "data.results.0.url".</summary>
    internal static string? ReadPath(JsonNode? node, string path)
    {
        if (node is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var current = node;
        foreach (var rawPart in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (current is JsonArray array)
            {
                if (!int.TryParse(part, out var index) || index < 0 || index >= array.Count)
                {
                    return null;
                }

                current = array[index];
                continue;
            }

            if (current is JsonObject jsonObject && jsonObject.TryGetPropertyValue(part, out var next))
            {
                current = next;
                continue;
            }

            return null;
        }

        return current switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonArray array when array.Count > 0 && array[0] is JsonValue first && first.TryGetValue<string>(out var firstText) => firstText,
            _ => current.ToJsonString().Trim('"'),
        };
    }
}
