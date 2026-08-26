using System.Globalization;
using System.Text;
using Lanshu.Presenter.Core.Media;

namespace Lanshu.Presenter.Core.Presenter;

/// <summary>
/// Builds an ffmpeg <c>sendcmd</c> script that pans a fixed-size crop window frame by frame.
///
/// An expression cannot read audio, and a nested conditional with one branch per frame would be
/// megabytes long. A sendcmd script is the idiomatic way to drive a filter from data computed
/// outside ffmpeg: one line per frame, read from a file.
///
/// Only <c>x</c> and <c>y</c> are commanded. Changing the crop's width or height per frame would
/// change the output frame size mid-stream, which the encoder cannot accept — the zoom stays in
/// the zoompan stage, which is a pure function of frame number and needs no audio.
/// </summary>
public static class MotionScript
{
    public sealed record Options
    {
        /// <summary>Size of the plate the crop window moves across.</summary>
        public required int SourceWidth { get; init; }

        public required int SourceHeight { get; init; }

        /// <summary>Size of the crop window, i.e. the delivered frame.</summary>
        public required int TargetWidth { get; init; }

        public required int TargetHeight { get; init; }

        public required int Fps { get; init; }

        public required int FrameCount { get; init; }

        /// <summary>Horizontal sway in target pixels at full amplitude.</summary>
        public double SwayPixels { get; init; } = 10;

        public double BreathPeriodSeconds { get; init; } = 5.5;

        /// <summary>How much of the movement comes from speech rather than the idle oscillation.</summary>
        public double AudioWeight { get; init; } = 0.65;
    }

    public static string Build(Options options, AudioEnvelope envelope)
    {
        var builder = new StringBuilder();

        var travelX = Math.Max(0, options.SourceWidth - options.TargetWidth);
        var travelY = Math.Max(0, options.SourceHeight - options.TargetHeight);
        var centreX = travelX / 2.0;
        var centreY = travelY / 2.0;

        // The plate is larger than the frame, so a target pixel of sway is more than one source
        // pixel; scale the amplitude into source space.
        var scale = options.TargetWidth > 0 ? (double)options.SourceWidth / options.TargetWidth : 1.0;
        var swayAmplitude = Math.Min(options.SwayPixels * scale, centreX);
        var bobAmplitude = Math.Min(options.SwayPixels * 0.6 * scale, centreY);

        var period = options.BreathPeriodSeconds <= 0.5 ? 5.5 : options.BreathPeriodSeconds;
        var audioWeight = Math.Clamp(options.AudioWeight, 0, 1);
        var idleWeight = 1 - audioWeight;

        var lastX = int.MinValue;
        var lastY = int.MinValue;

        for (var frame = 0; frame < options.FrameCount; frame++)
        {
            var time = (double)frame / options.Fps;
            var speech = envelope.At(frame);

            // Idle drift keeps the presenter alive between phrases; the speech term makes the
            // movement track what is actually being said.
            var idleX = Math.Sin(2 * Math.PI * time / period);
            var speechX = Math.Sin(2 * Math.PI * time / (period * 0.42)) * speech;
            var offsetX = swayAmplitude * ((idleWeight * idleX) + (audioWeight * speechX));

            var idleY = Math.Sin(2 * Math.PI * time / (period * 1.7));
            var offsetY = bobAmplitude * ((idleWeight * idleY) - (audioWeight * speech * 0.5));

            var x = (int)Math.Round(Math.Clamp(centreX + offsetX, 0, travelX));
            var y = (int)Math.Round(Math.Clamp(centreY + offsetY, 0, travelY));

            // Only emit when a value actually changes; a static frame needs no command.
            if (x == lastX && y == lastY)
            {
                continue;
            }

            builder.Append(time.ToString("0.######", CultureInfo.InvariantCulture));
            builder.Append(" crop x ").Append(x.ToString(CultureInfo.InvariantCulture));
            builder.Append(", crop y ").Append(y.ToString(CultureInfo.InvariantCulture));
            builder.Append(";\n");

            lastX = x;
            lastY = y;
        }

        return builder.ToString();
    }

    /// <summary>
    /// A filtergraph path expects the script by name from its working directory, which avoids
    /// every drive-letter and backslash escaping trap on Windows.
    /// </summary>
    public const string FileName = "motion.cmd";
}
