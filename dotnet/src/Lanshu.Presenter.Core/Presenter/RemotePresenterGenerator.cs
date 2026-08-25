using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Presenter;

/// <summary>
/// Talking-head generation through a configured provider. Every request body, task id and
/// acceptance note is archived to qa/requests so an interrupted job can be polled instead of
/// resubmitted, as the recovery rules require.
/// </summary>
public sealed class RemotePresenterGenerator : IPresenterGenerator
{
    private readonly RemoteJobSettings _settings;
    private readonly RemoteJobClient _client;
    private readonly FfmpegService _ffmpeg;
    private readonly JobPaths _paths;
    private readonly Action<string>? _log;

    public RemotePresenterGenerator(
        RemoteJobSettings settings,
        SettingsStore store,
        HttpClient httpClient,
        FfmpegService ffmpeg,
        JobPaths paths,
        Action<string>? log = null)
    {
        _settings = settings;
        _client = new RemoteJobClient(settings, store, httpClient, log);
        _ffmpeg = ffmpeg;
        _paths = paths;
        _log = log;
    }

    public string Provider => string.IsNullOrWhiteSpace(_settings.Provider) ? "remote-presenter" : _settings.Provider;

    public string Model => _settings.Model;

    public bool IsRemote => true;

    public bool ProducesLipSync => true;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_settings.IsConfigured);

    public async Task<PresenterPlate> GenerateAsync(
        PresenterRequest request,
        CancellationToken cancellationToken = default)
    {
        var inputs = new RemoteJobInputs
        {
            ImagePath = request.ImagePath,
            AudioPath = request.AudioPath,
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            Width = request.Width,
            Height = request.Height,
            Fps = request.Fps,
            DurationSeconds = request.DurationSeconds,
        };

        var outcome = await _client
            .RunAsync(inputs, request.OutputPath, cancellationToken)
            .ConfigureAwait(false);

        ArchiveRequest(outcome, request.IsPilot);

        var probe = await _ffmpeg.ProbeAsync(request.OutputPath, cancellationToken).ConfigureAwait(false);
        if (!probe.HasVideo)
        {
            throw new PresenterGenerationException("the remote result has no decodable video stream");
        }

        _log?.Invoke($"Remote presenter accepted: {probe.DurationSeconds:0.00}s, {probe.Video?.Width}x{probe.Video?.Height}");

        return new PresenterPlate(
            request.OutputPath,
            probe.DurationSeconds,
            Provider,
            Model,
            HasSynchronizedMouth: true,
            string.IsNullOrWhiteSpace(outcome.TaskId) ? Array.Empty<string>() : new[] { outcome.TaskId });
    }

    private void ArchiveRequest(RemoteJobOutcome outcome, bool isPilot)
    {
        Directory.CreateDirectory(_paths.QaRequests);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var name = $"{stamp}-{(isPilot ? "pilot" : "presenter")}.json";
        var payload = $$"""
            {
              "provider": {{RemoteJobClient.Quote(Provider)}},
              "model": {{RemoteJobClient.Quote(Model)}},
              "version": {{RemoteJobClient.Quote(_settings.Version)}},
              "region": {{RemoteJobClient.Quote(_settings.Region)}},
              "task_id": {{RemoteJobClient.Quote(outcome.TaskId)}},
              "pilot": {{(isPilot ? "true" : "false")}},
              "submitted_utc": {{RemoteJobClient.Quote(DateTimeOffset.UtcNow.ToString("O"))}},
              "request": {{outcome.SanitizedRequest}}
            }
            """;

        FileSystemUtil.WriteAtomic(Path.Combine(_paths.QaRequests, name), payload + "\n");
    }
}

/// <summary>
/// Replaces mouth timing on an accepted motion plate using the exact locked audio, without
/// extending the duration. Used when the body performance is good but the lips are not.
/// </summary>
public sealed class LipSyncRepairService
{
    private readonly RemoteJobSettings _settings;
    private readonly RemoteJobClient _client;
    private readonly FfmpegService _ffmpeg;
    private readonly JobPaths _paths;
    private readonly Action<string>? _log;

    public LipSyncRepairService(
        RemoteJobSettings settings,
        SettingsStore store,
        HttpClient httpClient,
        FfmpegService ffmpeg,
        JobPaths paths,
        Action<string>? log = null)
    {
        _settings = settings;
        _client = new RemoteJobClient(settings, store, httpClient, log);
        _ffmpeg = ffmpeg;
        _paths = paths;
        _log = log;
    }

    public string Provider => string.IsNullOrWhiteSpace(_settings.Provider) ? "remote-lipsync" : _settings.Provider;

    public string Model => _settings.Model;

    public bool IsConfigured => _settings.IsConfigured;

    public async Task<PresenterPlate> RepairAsync(
        string motionPlatePath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
        {
            throw new PresenterGenerationException("no lip-sync provider is configured");
        }

        var sourceDuration = await _ffmpeg.DurationAsync(motionPlatePath, cancellationToken).ConfigureAwait(false);

        var inputs = new RemoteJobInputs
        {
            VideoPath = motionPlatePath,
            AudioPath = audioPath,
            DurationSeconds = sourceDuration,
        };

        _log?.Invoke("Applying lip-sync repair to the accepted motion plate");
        var outcome = await _client.RunAsync(inputs, outputPath, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(_paths.QaRequests);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        FileSystemUtil.WriteAtomic(
            Path.Combine(_paths.QaRequests, $"{stamp}-lipsync.json"),
            $$"""
              {
                "provider": {{RemoteJobClient.Quote(Provider)}},
                "model": {{RemoteJobClient.Quote(Model)}},
                "task_id": {{RemoteJobClient.Quote(outcome.TaskId)}},
                "submitted_utc": {{RemoteJobClient.Quote(DateTimeOffset.UtcNow.ToString("O"))}},
                "request": {{outcome.SanitizedRequest}}
              }
              """ + "\n");

        var probe = await _ffmpeg.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
        if (!probe.HasVideo)
        {
            throw new PresenterGenerationException("the lip-sync result has no decodable video stream");
        }

        // The repair must not stretch the take; a longer result would drift against the timeline.
        if (probe.DurationSeconds > sourceDuration + 0.5)
        {
            _log?.Invoke(
                $"Lip-sync result is {probe.DurationSeconds:0.00}s against a {sourceDuration:0.00}s plate; it will be trimmed on the timeline.");
        }

        return new PresenterPlate(
            outputPath,
            probe.DurationSeconds,
            Provider,
            Model,
            HasSynchronizedMouth: true,
            string.IsNullOrWhiteSpace(outcome.TaskId) ? Array.Empty<string>() : new[] { outcome.TaskId });
    }
}
