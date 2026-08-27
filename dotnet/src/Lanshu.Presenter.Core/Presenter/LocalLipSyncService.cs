using System.Globalization;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Presenter;

/// <summary>
/// Drives a lip-sync tool installed on this machine — Wav2Lip, SadTalker, video-retalking, or
/// anything else with a command-line interface. The command and its arguments are a template, so
/// no specific tool is baked in and a new one needs no code change.
///
/// This is the offline path to real mouth synchronization. Nothing is downloaded automatically:
/// these tools carry multi-gigabyte model weights and their own licences, so the operator
/// installs one and points the studio at it.
/// </summary>
public sealed class LocalLipSyncService
{
    private readonly LocalLipSyncSettings _settings;
    private readonly FfmpegService _ffmpeg;
    private readonly JobPaths _paths;
    private readonly Action<string>? _log;

    public LocalLipSyncService(
        LocalLipSyncSettings settings,
        FfmpegService ffmpeg,
        JobPaths paths,
        Action<string>? log = null)
    {
        _settings = settings;
        _ffmpeg = ffmpeg;
        _paths = paths;
        _log = log;
    }

    public string Provider => string.IsNullOrWhiteSpace(_settings.Provider) ? "local-lipsync" : _settings.Provider;

    public string Model => string.IsNullOrWhiteSpace(_settings.CheckpointPath)
        ? string.Empty
        : Path.GetFileNameWithoutExtension(_settings.CheckpointPath);

    /// <summary>True when the executable resolves and any required checkpoint is present.</summary>
    public bool IsConfigured
    {
        get
        {
            if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.Command) || string.IsNullOrWhiteSpace(_settings.Arguments))
            {
                return false;
            }

            if (ResolveCommand() is null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_settings.CheckpointPath)
                && !File.Exists(FileSystemUtil.ExpandPath(_settings.CheckpointPath)))
            {
                return false;
            }

            return true;
        }
    }

    private string? ResolveCommand() => ToolLocatorFind(_settings.Command);

    private static string? ToolLocatorFind(string command)
    {
        var expanded = command.Trim();
        if (expanded.Length == 0)
        {
            return null;
        }

        // An absolute or relative path is used as given; a bare name is looked up like a shell would.
        if (expanded.Contains(Path.DirectorySeparatorChar) || expanded.Contains('/'))
        {
            var full = FileSystemUtil.ExpandPath(expanded);
            return File.Exists(full) ? full : null;
        }

        return Environment.ToolLocator.Find(expanded);
    }

    public async Task<PresenterPlate> RepairAsync(
        string motionPlatePath,
        string audioPath,
        string imagePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var command = ResolveCommand()
            ?? throw new PresenterGenerationException(
                $"the local lip-sync command '{_settings.Command}' was not found");

        var sourceDuration = await _ffmpeg.DurationAsync(motionPlatePath, cancellationToken).ConfigureAwait(false);
        var arguments = BuildArguments(motionPlatePath, audioPath, imagePath, outputPath);

        FileSystemUtil.TryDelete(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        _log?.Invoke($"Running local lip-sync: {Path.GetFileName(command)} {string.Join(' ', arguments)}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_settings.TimeoutSeconds, 60, 21600)));

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                    command,
                    arguments,
                    workingDirectory: string.IsNullOrWhiteSpace(_settings.WorkingDirectory)
                        ? null
                        : FileSystemUtil.ExpandPath(_settings.WorkingDirectory),
                    onOutputLine: line => _log?.Invoke("lipsync: " + line),
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PresenterGenerationException(
                $"the local lip-sync tool did not finish within {_settings.TimeoutSeconds:0} seconds");
        }

        if (!result.Success)
        {
            throw new PresenterGenerationException(
                "the local lip-sync tool failed: " + ProcessResult.Tail(result.Combined, 12));
        }

        if (!File.Exists(outputPath) || FileSystemUtil.SafeLength(outputPath) == 0)
        {
            throw new PresenterGenerationException(
                "the local lip-sync tool reported success but wrote no video");
        }

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

        ArchiveRun(command, arguments, probe.DurationSeconds);

        return new PresenterPlate(
            outputPath,
            probe.DurationSeconds,
            Provider,
            Model,
            HasSynchronizedMouth: true,
            Array.Empty<string>());
    }

    internal IReadOnlyList<string> BuildArguments(
        string videoPath,
        string audioPath,
        string imagePath,
        string outputPath)
    {
        var checkpoint = string.IsNullOrWhiteSpace(_settings.CheckpointPath)
            ? string.Empty
            : FileSystemUtil.ExpandPath(_settings.CheckpointPath);

        // Arguments are split on whitespace before substitution so a path containing spaces
        // stays a single argument instead of being torn apart by its own contents.
        return _settings.Arguments
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token
                .Replace("{{VIDEO}}", videoPath, StringComparison.Ordinal)
                .Replace("{{AUDIO}}", audioPath, StringComparison.Ordinal)
                .Replace("{{IMAGE}}", imagePath, StringComparison.Ordinal)
                .Replace("{{OUTPUT}}", outputPath, StringComparison.Ordinal)
                .Replace("{{CHECKPOINT}}", checkpoint, StringComparison.Ordinal)
                .Replace("{{OUTPUT_DIR}}", Path.GetDirectoryName(Path.GetFullPath(outputPath))!, StringComparison.Ordinal))
            .ToList();
    }

    private void ArchiveRun(string command, IReadOnlyList<string> arguments, double duration)
    {
        Directory.CreateDirectory(_paths.QaRequests);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var payload = $$"""
            {
              "provider": {{RemoteJobClient.Quote(Provider)}},
              "model": {{RemoteJobClient.Quote(Model)}},
              "kind": "local-lipsync",
              "command": {{RemoteJobClient.Quote(Path.GetFileName(command))}},
              "arguments": {{RemoteJobClient.Quote(string.Join(' ', arguments.Select(Path.GetFileName)))}},
              "result_duration_s": {{duration.ToString("0.###", CultureInfo.InvariantCulture)}},
              "run_utc": {{RemoteJobClient.Quote(DateTimeOffset.UtcNow.ToString("O"))}}
            }
            """;

        FileSystemUtil.WriteAtomic(Path.Combine(_paths.QaRequests, $"{stamp}-local-lipsync.json"), payload + "\n");
    }

    /// <summary>
    /// Ready-made argument templates for the tools people actually install. Shown in Settings so
    /// the operator picks one instead of writing a command line from scratch.
    /// </summary>
    public static readonly IReadOnlyList<LipSyncPreset> Presets = new[]
    {
        new LipSyncPreset(
            "wav2lip",
            "Wav2Lip",
            "python",
            "inference.py --checkpoint_path {{CHECKPOINT}} --face {{VIDEO}} --audio {{AUDIO}} --outfile {{OUTPUT}}",
            "Point the working directory at your Wav2Lip checkout and the checkpoint at wav2lip_gan.pth."),
        new LipSyncPreset(
            "video-retalking",
            "video-retalking",
            "python",
            "inference.py --face {{VIDEO}} --audio {{AUDIO}} --outfile {{OUTPUT}}",
            "Point the working directory at your video-retalking checkout."),
        new LipSyncPreset(
            "sadtalker",
            "SadTalker",
            "python",
            "inference.py --driven_audio {{AUDIO}} --source_image {{IMAGE}} --result_dir {{OUTPUT_DIR}} --still --preprocess full",
            "SadTalker animates the still image directly and writes into a result directory rather than a named file."),
    };
}

public sealed record LipSyncPreset(string Id, string DisplayName, string Command, string Arguments, string Note);
