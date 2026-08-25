using System.Reflection;
using System.Runtime.InteropServices;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Util;
using Lanshu.Presenter.Core.Voice;

namespace Lanshu.Presenter.Core.Environment;

/// <summary>
/// The "doctor" behind the app's readiness panel: what is installed, what is configured, and what
/// still has to happen before a render can run.
/// </summary>
public sealed class EnvironmentService
{
    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public EnvironmentService(SettingsStore store, HttpClient httpClient)
    {
        _store = store;
        _httpClient = httpClient;
    }

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "1.0.0";

    public async Task<EnvironmentReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        var settings = _store.Load();
        var report = new EnvironmentReport
        {
            Platform = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            AppVersion = AppVersion,
            Workspace = settings.ResolvedWorkspace,
        };

        try
        {
            var toolset = await MediaToolset
                .ResolveAsync(settings.FfmpegPath, settings.FfprobePath, cancellationToken)
                .ConfigureAwait(false);

            report.FfmpegPath = toolset.FfmpegPath;
            report.FfprobePath = toolset.FfprobePath;
            report.FfmpegVersion = toolset.Version;
            report.SubtitleBurnIn = toolset.HasSubtitleBurnIn;
            report.DrawText = toolset.HasDrawText;
            report.ZoomPan = toolset.HasZoomPan;
            report.Libx264 = toolset.HasLibx264;
            report.Aac = toolset.HasAac;
            report.Loudnorm = toolset.HasLoudnorm;

            foreach (var missing in toolset.MissingRequirements())
            {
                report.Problems.Add($"the FFmpeg build is missing the {missing}");
            }

            if (!toolset.HasSubtitleBurnIn)
            {
                report.Notes.Add(
                    "this FFmpeg build cannot burn in subtitles; captions will be delivered as a sidecar .srt only");
            }

            if (!toolset.HasZoomPan)
            {
                report.Problems.Add(
                    "the FFmpeg build is missing the zoompan filter, which the local motion plate needs");
            }

            var selector = new VideoEncoderSelector(toolset);
            var encoder = await selector
                .ResolveAsync(settings.Render.Encoder, cancellationToken)
                .ConfigureAwait(false);
            report.VideoEncoder = encoder.DisplayName;
            report.HardwareEncoders =
                (await selector.AvailableHardwareAsync(cancellationToken).ConfigureAwait(false)).ToList();

            if (report.HardwareEncoders.Count == 0)
            {
                report.Notes.Add(
                    "no working hardware video encoder was found; renders use software encoding, which is slower but produces identical output");
            }
        }
        catch (Exception exception)
        {
            report.Problems.Add(exception.Message);
        }

        try
        {
            var router = new SpeechRouter(settings, _store, _httpClient);
            foreach (var synthesizer in await router.AvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                report.OfflineVoices.Add(synthesizer.Provider);
            }
        }
        catch (Exception exception)
        {
            report.Problems.Add("voice check failed: " + exception.Message);
        }

        if (report.OfflineVoices.Count == 0)
        {
            report.Problems.Add(
                ToolLocator.IsWindows
                    ? "no speech engine is available; Windows system voices could not be reached through PowerShell"
                    : "no speech engine is available; install espeak-ng or add an ElevenLabs, OpenAI, or Azure key");
        }

        foreach (var descriptor in SettingsStore.KnownSecrets)
        {
            if (_store.HasSecret(descriptor.Key))
            {
                report.ConfiguredProviders.Add(descriptor.DisplayName);
            }
        }

        if (settings.Presenter.Remote.IsConfigured)
        {
            report.ConfiguredProviders.Add(
                $"talking head: {(string.IsNullOrWhiteSpace(settings.Presenter.Remote.Provider) ? "configured endpoint" : settings.Presenter.Remote.Provider)}");
        }
        else
        {
            report.Notes.Add(
                "no talking-head provider is configured; the presenter track will be an animated motion plate rendered from the still image");
        }

        if (!settings.Presenter.LipSync.IsConfigured)
        {
            report.Notes.Add("no lip-sync repair provider is configured");
        }

        report.Ok = report.Problems.Count == 0;
        return report;
    }

    public async Task<string> InstallFfmpegAsync(
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = _store.Load();
        var url = string.IsNullOrWhiteSpace(settings.FfmpegDownloadUrl) ? null : settings.FfmpegDownloadUrl;
        var result = await new FfmpegInstaller(_httpClient)
            .InstallAsync(url, progress, cancellationToken)
            .ConfigureAwait(false);

        settings.FfmpegPath = result.FfmpegPath;
        settings.FfprobePath = result.FfprobePath;
        _store.Save(settings);
        return result.FfmpegPath;
    }
}
