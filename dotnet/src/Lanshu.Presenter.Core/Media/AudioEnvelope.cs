using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Media;

/// <summary>
/// A per-frame loudness curve for the narration, normalized to 0..1. Used to drive presenter
/// motion from what is actually being said instead of from a fixed oscillator.
/// </summary>
public sealed class AudioEnvelope
{
    private readonly double[] _values;

    private AudioEnvelope(double[] values, int fps)
    {
        _values = values;
        Fps = fps;
    }

    public int Fps { get; }

    public int FrameCount => _values.Length;

    /// <summary>Envelope value at a frame, clamped at both ends so callers need no bounds checks.</summary>
    public double At(int frame)
    {
        if (_values.Length == 0)
        {
            return 0;
        }

        return _values[Math.Clamp(frame, 0, _values.Length - 1)];
    }

    public static AudioEnvelope Silent(int frameCount, int fps) =>
        new(new double[Math.Max(0, frameCount)], fps);

    /// <summary>
    /// Decodes the audio to mono PCM and reduces it to one RMS value per video frame, then
    /// applies an asymmetric smoothing: motion should follow speech onsets quickly but settle
    /// slowly, which is how a person actually moves.
    /// </summary>
    public static async Task<AudioEnvelope> MeasureAsync(
        FfmpegService ffmpeg,
        string audioPath,
        int fps,
        int frameCount,
        CancellationToken cancellationToken = default)
    {
        if (fps <= 0 || frameCount <= 0 || !File.Exists(audioPath))
        {
            return Silent(frameCount, Math.Max(1, fps));
        }

        // 8 kHz mono is far more resolution than a motion curve needs and keeps the decode cheap.
        const int sampleRate = 8000;
        var scratch = Path.Combine(Path.GetTempPath(), $"lanshu-env-{Guid.NewGuid():N}.raw");

        try
        {
            await ffmpeg.RunCheckedAsync(
                    "envelope decode",
                    new[]
                    {
                        "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                        "-i", audioPath,
                        "-map", "0:a:0", "-vn",
                        "-f", "s16le", "-acodec", "pcm_s16le",
                        "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "-ac", "1",
                        scratch,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var bytes = await File.ReadAllBytesAsync(scratch, cancellationToken).ConfigureAwait(false);
            return FromPcm(bytes, sampleRate, fps, frameCount);
        }
        catch (Exception)
        {
            // A motion curve is a nicety; never fail a render because it could not be measured.
            return Silent(frameCount, fps);
        }
        finally
        {
            FileSystemUtil.TryDelete(scratch);
        }
    }

    internal static AudioEnvelope FromPcm(byte[] pcm, int sampleRate, int fps, int frameCount)
    {
        var raw = new double[frameCount];
        var samplesPerFrame = Math.Max(1, sampleRate / fps);
        var sampleCount = pcm.Length / 2;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var start = frame * samplesPerFrame;
            if (start >= sampleCount)
            {
                break;
            }

            var end = Math.Min(sampleCount, start + samplesPerFrame);
            double sum = 0;
            for (var index = start; index < end; index++)
            {
                var sample = BitConverter.ToInt16(pcm, index * 2) / 32768.0;
                sum += sample * sample;
            }

            raw[frame] = Math.Sqrt(sum / Math.Max(1, end - start));
        }

        // Normalize against a high percentile rather than the peak, so one loud plosive does not
        // flatten the whole curve.
        var sorted = raw.Where(value => value > 0).OrderBy(value => value).ToArray();
        var reference = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.95)] : 0;
        if (reference <= 1e-6)
        {
            return new AudioEnvelope(new double[frameCount], fps);
        }

        // Attack over roughly 60ms, release over roughly 400ms.
        var attack = 1 - Math.Exp(-1.0 / Math.Max(1, fps * 0.06));
        var release = 1 - Math.Exp(-1.0 / Math.Max(1, fps * 0.40));

        var smoothed = new double[frameCount];
        double current = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var target = Math.Clamp(raw[frame] / reference, 0, 1);
            var coefficient = target > current ? attack : release;
            current += (target - current) * coefficient;
            smoothed[frame] = current;
        }

        return new AudioEnvelope(smoothed, fps);
    }
}
