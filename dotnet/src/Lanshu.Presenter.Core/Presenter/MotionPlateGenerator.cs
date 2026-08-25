using System.Globalization;
using System.Text;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Media;

namespace Lanshu.Presenter.Core.Presenter;

/// <summary>
/// Renders an animated presenter plate from the still reference image with no network call and
/// no cost: a slow Ken Burns push, a breathing sway, an optional blurred backdrop when the source
/// aspect does not match the delivery aspect, plus a light grade and vignette.
///
/// This is a motion plate, not a talking head. It never claims synchronized lips — when a
/// talking-head or lip-sync provider is configured the pipeline prefers that instead, and the
/// job record always states which one actually ran.
/// </summary>
public sealed class MotionPlateGenerator : IPresenterGenerator
{
    private readonly FfmpegService _ffmpeg;
    private readonly MotionPlateSettings _settings;
    private readonly Action<string>? _log;

    public MotionPlateGenerator(FfmpegService ffmpeg, MotionPlateSettings settings, Action<string>? log = null)
    {
        _ffmpeg = ffmpeg;
        _settings = settings;
        _log = log;
    }

    public string Provider => "local-motion-plate";

    public string Model => "ffmpeg-kenburns";

    public bool IsRemote => false;

    public bool ProducesLipSync => false;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_ffmpeg.Toolset.HasZoomPan && _ffmpeg.Toolset.HasLibx264);

    public async Task<PresenterPlate> GenerateAsync(
        PresenterRequest request,
        CancellationToken cancellationToken = default)
    {
        var probe = await _ffmpeg.ProbeAsync(request.ImagePath, cancellationToken).ConfigureAwait(false);
        var stream = probe.Video
            ?? throw new PresenterGenerationException("the presenter image has no decodable image stream");

        var filter = BuildFilter(
            stream.Width,
            stream.Height,
            request.Width,
            request.Height,
            request.Fps,
            request.DurationSeconds);

        _log?.Invoke($"Rendering motion plate {request.Width}x{request.Height} @ {request.Fps}fps for {request.DurationSeconds:0.00}s");

        await _ffmpeg.RunEncodeAsync(
            "presenter motion plate",
            request.IsPilot ? EncodeQuality.Preview : EncodeQuality.Intermediate,
            encoderArguments =>
            {
                var arguments = new List<string>
                {
                    "-hide_banner", "-nostdin", "-loglevel", "warning", "-stats", "-y",
                    "-loop", "1",
                    "-framerate", request.Fps.ToString(CultureInfo.InvariantCulture),
                    "-t", request.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    "-i", request.ImagePath,
                    "-filter_complex", filter,
                    "-map", "[plate]",
                    "-an",
                };

                arguments.AddRange(encoderArguments);
                arguments.AddRange(new[]
                {
                    "-pix_fmt", "yuv420p",
                    "-r", request.Fps.ToString(CultureInfo.InvariantCulture),
                    "-fps_mode", "cfr",
                    request.OutputPath,
                });

                return arguments;
            },
            cancellationToken).ConfigureAwait(false);
        var duration = await _ffmpeg.DurationAsync(request.OutputPath, cancellationToken).ConfigureAwait(false);
        return PresenterPlate.Local(request.OutputPath, duration, Provider, Model);
    }

    /// <summary>
    /// Chooses between a cover crop and a contained plate with a blurred backdrop. A cover crop
    /// that would discard more than a third of the frame risks cutting the face, so the
    /// contained layout wins there.
    /// </summary>
    internal string BuildFilter(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        int fps,
        double durationSeconds)
    {
        var totalFrames = Math.Max(2, (int)Math.Round(durationSeconds * fps));
        var zoom = Math.Clamp(_settings.ZoomPercent, 0, 25) / 100.0;
        var period = _settings.BreathPeriodSeconds <= 0.5 ? 5.5 : _settings.BreathPeriodSeconds;

        // Work at twice the delivery size so zoompan's integer stepping stays invisible.
        var workWidth = EnsureEven(targetWidth * 2);
        var workHeight = EnsureEven(targetHeight * 2);
        var sway = Math.Clamp(_settings.SwayPixels, 0, 60) * 2;
        var bob = sway * 0.6;

        var mode = ResolveBackground(sourceWidth, sourceHeight, targetWidth, targetHeight);
        var filter = new StringBuilder();

        if (mode == "cover")
        {
            filter.Append(CultureInfo.InvariantCulture,
                $"[0:v]scale={workWidth}:{workHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop={workWidth}:{workHeight},setsar=1[src];");
        }
        else
        {
            // Blurred cover behind, the whole reference image contained in front.
            filter.Append(CultureInfo.InvariantCulture,
                $"[0:v]split=2[bgsrc][fgsrc];");
            filter.Append(CultureInfo.InvariantCulture,
                $"[bgsrc]scale={workWidth}:{workHeight}:force_original_aspect_ratio=increase:flags=lanczos,crop={workWidth}:{workHeight},gblur=sigma={Math.Max(12, workWidth / 90)},eq=brightness=-0.09:saturation=0.55,setsar=1[bg];");
            filter.Append(CultureInfo.InvariantCulture,
                $"[fgsrc]scale={workWidth}:{workHeight}:force_original_aspect_ratio=decrease:flags=lanczos,setsar=1[fg];");
            filter.Append("[bg][fg]overlay=(W-w)/2:(H-h)/2:format=auto,setsar=1[src];");
        }

        // Ease the push in and out with a raised cosine so the motion never starts or stops abruptly.
        var zoomExpression = string.Format(
            CultureInfo.InvariantCulture,
            "1+{0:0.#####}*(0.5-0.5*cos(2*PI*on/{1}))",
            zoom,
            totalFrames);

        var xExpression = string.Format(
            CultureInfo.InvariantCulture,
            "iw/2-(iw/zoom/2)+{0:0.##}*sin(2*PI*on/{1:0.##})",
            sway,
            fps * period);

        var yExpression = string.Format(
            CultureInfo.InvariantCulture,
            "ih/2-(ih/zoom/2)+{0:0.##}*sin(2*PI*on/{1:0.##})",
            bob,
            fps * period * 1.7);

        filter.Append(CultureInfo.InvariantCulture,
            $"[src]zoompan=z='{zoomExpression}':x='{xExpression}':y='{yExpression}':d=1:s={targetWidth}x{targetHeight}:fps={fps}");

        if (_settings.Grade)
        {
            filter.Append(",eq=contrast=1.05:saturation=1.06:gamma=1.01");
        }

        if (_settings.Vignette)
        {
            filter.Append(",vignette=PI/5");
        }

        filter.Append(",format=yuv420p[plate]");
        return filter.ToString();
    }

    private string ResolveBackground(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var configured = (_settings.Background ?? "blurred").Trim().ToLowerInvariant();
        if (configured is "cover" or "contain" or "blurred")
        {
            if (configured == "cover")
            {
                return "cover";
            }

            if (configured == "contain")
            {
                return "blurred";
            }
        }

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return "blurred";
        }

        var sourceAspect = (double)sourceWidth / sourceHeight;
        var targetAspect = (double)targetWidth / targetHeight;
        var ratio = sourceAspect > targetAspect ? sourceAspect / targetAspect : targetAspect / sourceAspect;

        // Beyond about a third of the frame lost, a cover crop starts cutting heads off.
        return ratio <= 1.5 ? "cover" : "blurred";
    }

    private static int EnsureEven(int value) => value % 2 == 0 ? value : value + 1;
}
