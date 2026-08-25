using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;

namespace Lanshu.Presenter.Core.Presenter;

public sealed record PresenterRoute(IPresenterGenerator Generator, bool RequiresApproval, string Reason);

/// <summary>
/// Capability routing for the presenter track. A configured talking-head provider wins when the
/// job's rights, adult-status and upload approvals are recorded; otherwise the local motion plate
/// runs so a video is always produced.
/// </summary>
public sealed class PresenterRouter
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;
    private readonly FfmpegService _ffmpeg;
    private readonly Action<string>? _log;

    public PresenterRouter(
        AppSettings settings,
        SettingsStore store,
        HttpClient httpClient,
        FfmpegService ffmpeg,
        Action<string>? log = null)
    {
        _settings = settings;
        _store = store;
        _httpClient = httpClient;
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public PresenterRoute Resolve(JobManifest job, JobPaths paths)
    {
        var mode = (_settings.Presenter.Mode ?? "auto").Trim().ToLowerInvariant();
        var remoteSettings = _settings.Presenter.Remote;
        var motion = new MotionPlateGenerator(_ffmpeg, _settings.Presenter.Motion, _log);

        if (mode == "motion")
        {
            return new PresenterRoute(motion, false, "motion plate selected in Settings");
        }

        if (!remoteSettings.IsConfigured)
        {
            return mode == "remote"
                ? throw new PresenterGenerationException(
                    "presenter mode is set to 'remote' but no submit URL and request template are configured")
                : new PresenterRoute(motion, false, "no talking-head provider is configured");
        }

        var approvals = MissingApprovals(job);
        if (approvals.Count > 0)
        {
            if (mode == "remote")
            {
                throw new PresenterGenerationException(
                    "remote generation is blocked until these are confirmed: " + string.Join(", ", approvals));
            }

            return new PresenterRoute(
                motion,
                false,
                "remote generation is blocked until these are confirmed: " + string.Join(", ", approvals));
        }

        var remote = new RemotePresenterGenerator(
            remoteSettings,
            _store,
            _httpClient,
            _ffmpeg,
            paths,
            _log);

        return new PresenterRoute(remote, true, $"talking-head provider '{remote.Provider}' is configured");
    }

    public static List<string> MissingApprovals(JobManifest job)
    {
        var missing = new List<string>();
        if (!job.Input.RightsConfirmed)
        {
            missing.Add("image rights");
        }

        if (!job.Input.AdultPresenterConfirmed)
        {
            missing.Add("adult presenter status");
        }

        if (!job.Input.RemoteUploadApproved)
        {
            missing.Add("remote upload approval");
        }

        return missing;
    }

    public LipSyncRepairService CreateLipSync(JobPaths paths) =>
        new(_settings.Presenter.LipSync, _store, _httpClient, _ffmpeg, paths, _log);

    /// <summary>
    /// The billing statement the skill requires before the first paid call: what is uploaded,
    /// how much is requested, the known price, the pilot size and the retry ceiling.
    /// </summary>
    public static string BuildCostDisclosure(
        JobManifest job,
        RemoteJobSettings settings,
        double requestedSeconds,
        double pilotSeconds)
    {
        var lines = new List<string>
        {
            $"Provider: {(string.IsNullOrWhiteSpace(settings.Provider) ? "configured endpoint" : settings.Provider)}"
            + (string.IsNullOrWhiteSpace(settings.Model) ? string.Empty : $" / {settings.Model}"),
            $"Endpoint: {settings.SubmitUrl}",
            "Uploaded: the presenter image and the locked narration audio",
            $"Requested: {requestedSeconds:0.0}s of generation (pilot first: {pilotSeconds:0.0}s)",
        };

        lines.Add(settings.PricePerSecond is { } price
            ? $"Known price: {price:0.####} per second, about {price * requestedSeconds:0.##} for the full run"
              + (string.IsNullOrWhiteSpace(settings.PriceEvidenceDate)
                  ? " (no price evidence date recorded)"
                  : $" (price evidence dated {settings.PriceEvidenceDate})")
            : "Known price: not recorded. Confirm the current rate with the provider before approving.");

        lines.Add($"Retry ceiling: {job.Plan.RetryCeiling} rejected candidates, then the run stops and reports.");
        lines.Add("Expected output: one continuous presenter clip covering the full narration.");
        return string.Join("\n", lines);
    }
}
