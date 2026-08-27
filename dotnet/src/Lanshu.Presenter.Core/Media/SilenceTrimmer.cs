using System.Globalization;
using System.Text.RegularExpressions;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Media;

public sealed record SilenceTrim(double LeadSeconds, double TailSeconds, double DurationSeconds)
{
    public double TotalSeconds => LeadSeconds + TailSeconds;

    public bool Trimmed => TotalSeconds > 0.001;

    public static readonly SilenceTrim None = new(0, 0, 0);
}

/// <summary>
/// Removes the dead air a speech engine pads onto a segment. Engines differ by hundreds of
/// milliseconds at each end, which is what makes synthesized narration sound slack between
/// sentences — the gaps the assembly lays out deliberately are then stacked on top of padding
/// nobody asked for.
///
/// Only the head and tail are cut. Silence inside a sentence is the speaker breathing and
/// punctuating, and removing it is what makes TTS sound like it is gabbling.
/// </summary>
public static class SilenceTrimmer
{
    /// <summary>Anything under this is silence for our purposes; normalized speech sits far above it.</summary>
    private const string ThresholdDb = "-45dB";

    /// <summary>A hard cut on the first sample of speech clips the attack, so a little is kept.</summary>
    private const double PadSeconds = 0.035;

    /// <summary>Never cut a segment below this, whatever the detector claims.</summary>
    private const double MinimumKeptSeconds = 0.20;

    private static readonly Regex SilenceStart =
        new(@"silence_start:\s*(-?[0-9.]+)", RegexOptions.Compiled);

    private static readonly Regex SilenceEnd =
        new(@"silence_end:\s*(-?[0-9.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Trims <paramref name="path"/> in place. Returns what was removed; a file that is all
    /// silence, or that the detector cannot read, is left exactly as it was rather than
    /// truncated to nothing.
    /// </summary>
    public static async Task<SilenceTrim> TrimAsync(
        FfmpegService ffmpeg,
        string path,
        CancellationToken cancellationToken = default)
    {
        var duration = await ffmpeg.DurationAsync(path, cancellationToken).ConfigureAwait(false);
        if (duration <= 0)
        {
            return SilenceTrim.None;
        }

        var detect = await ffmpeg.RunAsync(
                new[]
                {
                    "-hide_banner", "-nostdin", "-i", path,
                    "-af", $"silencedetect=noise={ThresholdDb}:d=0.05",
                    "-f", "null", "-",
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!detect.Success)
        {
            return SilenceTrim.None;
        }

        var (start, end) = FindSpeechWindow(detect.Combined, duration);
        if (end - start < MinimumKeptSeconds)
        {
            return SilenceTrim.None;
        }

        if (start <= 0.001 && end >= duration - 0.001)
        {
            return SilenceTrim.None;
        }

        var trimmed = path + ".trim.wav";
        FileSystemUtil.TryDelete(trimmed);

        var result = await ffmpeg.RunAsync(
                new[]
                {
                    "-hide_banner", "-nostdin", "-y", "-i", path,
                    "-af", string.Create(CultureInfo.InvariantCulture,
                        $"atrim=start={start:0.###}:end={end:0.###},asetpts=N/SR/TB"),
                    "-c:a", "pcm_s16le",
                    trimmed,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success || FileSystemUtil.SafeLength(trimmed) <= 0)
        {
            FileSystemUtil.TryDelete(trimmed);
            return SilenceTrim.None;
        }

        File.Move(trimmed, path, overwrite: true);
        return new SilenceTrim(start, Math.Max(0, duration - end), end - start);
    }

    /// <summary>
    /// Reads silencedetect's log into the first and last moment of speech. A silence that starts
    /// at zero is leading padding; a silence that never ends is trailing padding. Both are given
    /// a small pad back so the attack and release of the speech survive the cut.
    /// </summary>
    internal static (double Start, double End) FindSpeechWindow(string log, double duration)
    {
        var starts = SilenceStart.Matches(log)
            .Select(match => Parse(match.Groups[1].Value))
            .ToList();
        var ends = SilenceEnd.Matches(log)
            .Select(match => Parse(match.Groups[1].Value))
            .ToList();

        var speechStart = 0.0;
        // Leading padding is a silence that begins at the very start of the file.
        if (starts.Count > 0 && starts[0] <= 0.05 && ends.Count > 0)
        {
            speechStart = Math.Max(0, ends[0] - PadSeconds);
        }

        var speechEnd = duration;
        // Trailing padding is a silence with no matching end: it runs to the end of the file.
        if (starts.Count > ends.Count)
        {
            speechEnd = Math.Min(duration, starts[^1] + PadSeconds);
        }

        if (speechEnd <= speechStart)
        {
            return (0, duration);
        }

        return (speechStart, speechEnd);
    }

    private static double Parse(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
}
