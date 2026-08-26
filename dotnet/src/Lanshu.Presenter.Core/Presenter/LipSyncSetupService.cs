using System.Globalization;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Presenter;

public sealed record LipSyncStatus(
    bool Configured,
    string Provider,
    string Command,
    string ResolvedCommand,
    string WorkingDirectory,
    string CheckpointPath,
    IReadOnlyList<string> Problems);

public sealed record LipSyncTestResult(bool Passed, string Detail, double DurationSeconds);

/// <summary>
/// Configures and verifies a locally installed lip-sync tool.
///
/// The tool itself is not downloaded: Wav2Lip and its peers ship multi-gigabyte weights under
/// their own licences, and fetching those silently on a user's behalf is not this app's call.
/// What this does remove is the fiddly part — picking the right command line for the tool you
/// installed, and proving it actually runs before a real job depends on it.
/// </summary>
public sealed class LipSyncSetupService
{
    private readonly SettingsStore _store;
    private readonly FfmpegService _ffmpeg;
    private readonly Action<string>? _log;

    public LipSyncSetupService(SettingsStore store, FfmpegService ffmpeg, Action<string>? log = null)
    {
        _store = store;
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public static IReadOnlyList<LipSyncPreset> Presets => LocalLipSyncService.Presets;

    public LipSyncStatus Describe()
    {
        var settings = _store.Load().Presenter.LocalLipSync;
        var problems = new List<string>();

        if (!settings.Enabled)
        {
            problems.Add("local lip-sync is switched off");
        }

        if (string.IsNullOrWhiteSpace(settings.Command))
        {
            problems.Add("no command is configured");
        }

        var resolved = string.IsNullOrWhiteSpace(settings.Command)
            ? string.Empty
            : Environment.ToolLocator.Find(settings.Command) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(settings.Command) && string.IsNullOrWhiteSpace(resolved))
        {
            problems.Add($"the command '{settings.Command}' was not found on this machine");
        }

        if (!string.IsNullOrWhiteSpace(settings.WorkingDirectory)
            && !Directory.Exists(FileSystemUtil.ExpandPath(settings.WorkingDirectory)))
        {
            problems.Add($"the working directory does not exist: {settings.WorkingDirectory}");
        }

        if (!string.IsNullOrWhiteSpace(settings.CheckpointPath)
            && !File.Exists(FileSystemUtil.ExpandPath(settings.CheckpointPath)))
        {
            problems.Add($"the checkpoint does not exist: {settings.CheckpointPath}");
        }

        if (settings.Arguments.Contains("{{CHECKPOINT}}", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(settings.CheckpointPath))
        {
            problems.Add("the arguments reference a checkpoint but none is configured");
        }

        return new LipSyncStatus(
            problems.Count == 0,
            settings.Provider,
            settings.Command,
            resolved,
            settings.WorkingDirectory,
            settings.CheckpointPath,
            problems);
    }

    /// <summary>Writes settings from a preset plus the paths of the operator's own checkout.</summary>
    public LipSyncStatus Configure(
        string presetId,
        string checkoutDirectory,
        string? checkpointPath = null,
        string? command = null)
    {
        var preset = Presets.FirstOrDefault(candidate =>
                         string.Equals(candidate.Id, presetId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ArgumentException(
                         $"unknown preset '{presetId}'. Known: {string.Join(", ", Presets.Select(p => p.Id))}");

        var directory = FileSystemUtil.ExpandPath(checkoutDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"the checkout directory does not exist: {directory}");
        }

        var settings = _store.Load();
        var target = settings.Presenter.LocalLipSync;

        target.Enabled = true;
        target.Provider = preset.Id;
        target.Command = string.IsNullOrWhiteSpace(command) ? preset.Command : command;
        target.Arguments = preset.Arguments;
        target.WorkingDirectory = directory;

        if (!string.IsNullOrWhiteSpace(checkpointPath))
        {
            target.CheckpointPath = FileSystemUtil.ExpandPath(checkpointPath);
        }
        else if (preset.Arguments.Contains("{{CHECKPOINT}}", StringComparison.Ordinal))
        {
            // Most checkouts keep weights in a predictable place; use one if it is already there.
            target.CheckpointPath = GuessCheckpoint(directory) ?? target.CheckpointPath;
        }

        _store.Save(settings);
        return Describe();
    }

    private static string? GuessCheckpoint(string directory)
    {
        var candidates = new[] { "checkpoints", "weights", "models", "." };
        foreach (var relative in candidates)
        {
            var folder = Path.Combine(directory, relative);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var match = Directory
                .EnumerateFiles(folder, "*.pth", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path.Contains("gan", StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path.Length)
                .FirstOrDefault();

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the configured tool over a two-second synthetic clip. Proving it works here is much
    /// cheaper than discovering it does not partway through a real render.
    /// </summary>
    public async Task<LipSyncTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var status = Describe();
        if (!status.Configured)
        {
            return new LipSyncTestResult(false, string.Join("; ", status.Problems), 0);
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"lanshu-lipsync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);

        try
        {
            var face = Path.Combine(scratch, "face.mp4");
            var audio = Path.Combine(scratch, "audio.wav");
            var output = Path.Combine(scratch, "out.mp4");

            _log?.Invoke("Building a two-second synthetic clip");

            // A flat colour plate and a tone are enough to prove the tool runs and writes video.
            await _ffmpeg.RunCheckedAsync("test plate", new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=0x8899AA:s=480x480:r=25:d=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", face,
            }, cancellationToken).ConfigureAwait(false);

            await _ffmpeg.RunCheckedAsync("test audio", new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "sine=frequency=220:duration=2",
                "-c:a", "pcm_s16le", "-ar", "16000", "-ac", "1", audio,
            }, cancellationToken).ConfigureAwait(false);

            var settings = _store.Load();
            var service = new LocalLipSyncService(
                settings.Presenter.LocalLipSync,
                _ffmpeg,
                new JobPaths(scratch),
                _log);

            var plate = await service
                .RepairAsync(face, audio, face, output, cancellationToken)
                .ConfigureAwait(false);

            return new LipSyncTestResult(
                true,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} produced {1:0.00}s of video",
                    status.Provider,
                    plate.DurationSeconds),
                plate.DurationSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LipSyncTestResult(false, exception.Message, 0);
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(scratch);
        }
    }
}
