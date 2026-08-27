using System.Globalization;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Timeline;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Presenter;

/// <summary>
/// Separates the presenter from their original background and composites them onto a new one,
/// producing a replacement still that the motion plate then animates as usual.
///
/// Doing this once on the still rather than per frame keeps the motion code untouched and means
/// any halo or edge problem is visible in a single image the operator can inspect, instead of
/// only showing up in a finished render.
/// </summary>
public sealed class BackgroundCompositor
{
    private readonly FfmpegService _ffmpeg;
    private readonly BackgroundSettings _settings;
    private readonly Action<string>? _log;

    public BackgroundCompositor(FfmpegService ffmpeg, BackgroundSettings settings, Action<string>? log = null)
    {
        _ffmpeg = ffmpeg;
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Returns the path of the image to animate: the original when replacement is off or fails,
    /// or a new composite. Never throws — a background is a nicety, not a reason to lose a render.
    /// </summary>
    public async Task<string> PrepareAsync(
        string imagePath,
        string workingDirectory,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled)
        {
            return imagePath;
        }

        try
        {
            Directory.CreateDirectory(workingDirectory);
            var cutout = await ResolveCutoutAsync(imagePath, workingDirectory, cancellationToken)
                .ConfigureAwait(false);

            var destination = Path.Combine(workingDirectory, "presenter-composited.png");
            FileSystemUtil.TryDelete(destination);

            var arguments = new List<string>
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-i", cutout,
            };

            var backdrop = (_settings.Backdrop ?? "blurred").Trim().ToLowerInvariant();
            var usesOriginal = backdrop == "blurred";
            var usesImage = backdrop == "image" && File.Exists(FileSystemUtil.ExpandPath(_settings.BackdropImage));

            if (usesOriginal)
            {
                arguments.AddRange(new[] { "-i", imagePath });
            }
            else if (usesImage)
            {
                arguments.AddRange(new[] { "-i", FileSystemUtil.ExpandPath(_settings.BackdropImage) });
            }

            arguments.AddRange(new[]
            {
                "-filter_complex", BuildFilter(backdrop, usesOriginal || usesImage, width, height),
                "-map", "[out]",
                "-frames:v", "1",
                destination,
            });

            await _ffmpeg.RunCheckedAsync("background replacement", arguments, cancellationToken)
                .ConfigureAwait(false);

            _log?.Invoke($"Background replaced using the '{_settings.Mode}' cutout on a '{backdrop}' backdrop");
            return destination;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log?.Invoke("Background replacement failed, keeping the original image: " + exception.Message);
            return imagePath;
        }
    }

    /// <summary>Produces an RGBA image of the presenter with the background removed.</summary>
    private async Task<string> ResolveCutoutAsync(
        string imagePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var mode = (_settings.Mode ?? "none").Trim().ToLowerInvariant();

        if (mode == "cutout")
        {
            return await RunCutoutToolAsync(imagePath, workingDirectory, cancellationToken).ConfigureAwait(false);
        }

        var destination = Path.Combine(workingDirectory, "presenter-cutout.png");
        FileSystemUtil.TryDelete(destination);

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", imagePath,
        };

        string filter;
        if (mode == "matte")
        {
            var matte = FileSystemUtil.ExpandPath(_settings.MattePath);
            if (!File.Exists(matte))
            {
                throw new PresenterGenerationException($"the alpha matte does not exist: {matte}");
            }

            arguments.AddRange(new[] { "-i", matte });

            // alphamerge takes the luminance of the second input as the first input's alpha.
            filter =
                "[0:v]format=rgba[fg];" +
                "[1:v]format=gray[mask];" +
                "[fg][mask]alphamerge[out]";
        }
        else
        {
            var key = ToFfmpegColor(_settings.ChromaColor);
            var similarity = Math.Clamp(_settings.Similarity, 0.01, 1.0);
            var blend = Math.Clamp(_settings.Blend, 0.0, 1.0);

            // despill after the key, or green bounce stays on hair and shoulders.
            filter = string.Format(
                CultureInfo.InvariantCulture,
                "[0:v]format=rgba,chromakey={0}:{1:0.###}:{2:0.###},despill=type=green[out]",
                key,
                similarity,
                blend);
        }

        arguments.AddRange(new[] { "-filter_complex", filter, "-map", "[out]", "-frames:v", "1", destination });
        await _ffmpeg.RunCheckedAsync("presenter cutout", arguments, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    private async Task<string> RunCutoutToolAsync(
        string imagePath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.CutoutCommand))
        {
            throw new PresenterGenerationException("cutout mode needs a command; set one in Settings");
        }

        var command = Environment.ToolLocator.Find(_settings.CutoutCommand)
            ?? throw new PresenterGenerationException($"the cutout command '{_settings.CutoutCommand}' was not found");

        var destination = Path.Combine(workingDirectory, "presenter-cutout.png");
        FileSystemUtil.TryDelete(destination);

        // Split before substitution so a path containing spaces stays one argument.
        var arguments = (_settings.CutoutArguments ?? string.Empty)
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token
                .Replace("{{INPUT}}", imagePath, StringComparison.Ordinal)
                .Replace("{{OUTPUT}}", destination, StringComparison.Ordinal))
            .ToList();

        var result = await ProcessRunner
            .RunAsync(command, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success || !File.Exists(destination) || FileSystemUtil.SafeLength(destination) == 0)
        {
            throw new PresenterGenerationException(
                "the cutout tool produced no image: " + ProcessResult.Tail(result.Combined, 8));
        }

        return destination;
    }

    private string BuildFilter(string backdrop, bool hasBackdropInput, int width, int height)
    {
        var colour = FfmpegCompositor.ToFfmpegColor(_settings.BackdropColor);

        var background = backdrop switch
        {
            // The original, blurred hard enough that nothing in it reads as detail.
            "blurred" => string.Format(
                CultureInfo.InvariantCulture,
                "[1:v]scale={0}:{1}:force_original_aspect_ratio=increase,crop={0}:{1},gblur=sigma={2},eq=brightness=-0.10:saturation=0.5[bg];",
                width,
                height,
                Math.Max(18, width / 45)),

            "image" when hasBackdropInput => string.Format(
                CultureInfo.InvariantCulture,
                "[1:v]scale={0}:{1}:force_original_aspect_ratio=increase,crop={0}:{1}[bg];",
                width,
                height),

            "gradient" => string.Format(
                CultureInfo.InvariantCulture,
                "color=c={0}:s={1}x{2},format=rgba,vignette=PI/3.5[bg];",
                colour,
                width,
                height),

            _ => string.Format(
                CultureInfo.InvariantCulture,
                "color=c={0}:s={1}x{2},format=rgba[bg];",
                colour,
                width,
                height),
        };

        // The presenter is contained rather than cropped: a cutout has no spare edges to lose.
        var foreground = string.Format(
            CultureInfo.InvariantCulture,
            "[0:v]format=rgba,scale={0}:{1}:force_original_aspect_ratio=decrease:flags=lanczos[fg];",
            width,
            height);

        return background + foreground + "[bg][fg]overlay=(W-w)/2:(H-h)/2:format=auto,format=rgba[out]";
    }

    internal static string ToFfmpegColor(string hex) => FfmpegCompositor.ToFfmpegColor(hex);
}
