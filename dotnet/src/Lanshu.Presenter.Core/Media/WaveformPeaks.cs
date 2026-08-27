using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Media;

public sealed record Waveform(IReadOnlyList<double> Peaks, double DurationSeconds)
{
    public static readonly Waveform Empty = new(Array.Empty<double>(), 0);
}

/// <summary>
/// Reduces narration to a small array of peak amplitudes for drawing.
///
/// The point of a waveform under an edit is to show where the speech is, so this keeps the peak
/// of each bucket rather than its mean: an average over a tenth of a second flattens the very
/// consonants that make a phrase boundary visible.
/// </summary>
public static class WaveformPeaks
{
    /// <summary>Enough detail to see phrases at any sane window width, small enough to send as JSON.</summary>
    public const int DefaultBuckets = 900;

    private const int SampleRate = 8000;

    public static async Task<Waveform> MeasureAsync(
        FfmpegService ffmpeg,
        string audioPath,
        int buckets = DefaultBuckets,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioPath) || buckets <= 0)
        {
            return Waveform.Empty;
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"lanshu-peaks-{Guid.NewGuid():N}.raw");

        try
        {
            var result = await ffmpeg.RunAsync(
                    new[]
                    {
                        "-hide_banner", "-nostdin", "-y", "-i", audioPath,
                        "-ac", "1", "-ar", SampleRate.ToString(),
                        "-f", "s16le", "-acodec", "pcm_s16le",
                        scratch,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return Waveform.Empty;
            }

            var pcm = await File.ReadAllBytesAsync(scratch, cancellationToken).ConfigureAwait(false);
            return FromPcm(pcm, SampleRate, buckets);
        }
        catch (Exception)
        {
            // A drawing aid is never worth failing a request over.
            return Waveform.Empty;
        }
        finally
        {
            FileSystemUtil.TryDelete(scratch);
        }
    }

    internal static Waveform FromPcm(byte[] pcm, int sampleRate, int buckets)
    {
        var sampleCount = pcm.Length / 2;
        if (sampleCount == 0 || buckets <= 0)
        {
            return Waveform.Empty;
        }

        var duration = sampleCount / (double)sampleRate;
        var peaks = new double[buckets];
        var perBucket = sampleCount / (double)buckets;

        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var from = Math.Min(sampleCount - 1, (int)(bucket * perBucket));
            var to = Math.Min(sampleCount, (int)((bucket + 1) * perBucket));

            // With more buckets than samples a bucket can span nothing at all. Reading one sample
            // draws the clip as it is; holding zero would put a false gap of silence at the front.
            if (to <= from)
            {
                to = from + 1;
            }

            var peak = 0;
            for (var index = from; index < to; index++)
            {
                // Widen before taking the magnitude: Math.Abs(short) throws on short.MinValue,
                // and a normalized take really does reach full scale.
                var sample = Math.Abs((int)BitConverter.ToInt16(pcm, index * 2));
                if (sample > peak)
                {
                    peak = sample;
                }
            }

            peaks[bucket] = peak / (double)short.MaxValue;
        }

        return new Waveform(peaks, duration);
    }
}
