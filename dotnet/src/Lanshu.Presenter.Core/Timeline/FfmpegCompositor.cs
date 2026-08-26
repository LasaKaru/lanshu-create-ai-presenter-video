using System.Globalization;
using System.Text;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Timeline;

/// <summary>
/// Deterministic renderer for a <see cref="RenderTimeline"/>. Every video source is muted and the
/// approved narration is the only program clock; inserts, captions, keyword graphics, the
/// watermark and the progress line are composited on top in one pass.
/// </summary>
public sealed class FfmpegCompositor
{
    private readonly FfmpegService _ffmpeg;
    private readonly Action<string>? _log;

    public FfmpegCompositor(FfmpegService ffmpeg, Action<string>? log = null)
    {
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public async Task<string> RenderAsync(
        RenderTimeline timeline,
        string outputPath,
        CancellationToken cancellationToken = default,
        EncodeQuality quality = EncodeQuality.Master)
    {
        var presenter = timeline.Clips.FirstOrDefault(clip => clip.KindValue == ClipKind.Presenter)
            ?? throw new InvalidOperationException("the timeline has no presenter clip");

        var inserts = timeline.Clips.Where(clip => clip.KindValue == ClipKind.Insert).ToList();
        var duration = timeline.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-stats", "-y",
        };

        // Input 0: the presenter plate, seeked by its own source offset and looped so a short
        // take still covers the full program without a source-time reset.
        if (presenter.SourceOffsetSeconds > 0)
        {
            arguments.AddRange(new[] { "-ss", presenter.SourceOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture) });
        }

        arguments.AddRange(new[] { "-stream_loop", "-1", "-i", presenter.Source });

        // Input 1: the locked narration.
        arguments.AddRange(new[] { "-i", timeline.NarrationPath });

        var nextInput = 2;
        var musicInput = -1;
        if (!string.IsNullOrWhiteSpace(timeline.MusicPath) && File.Exists(timeline.MusicPath))
        {
            musicInput = nextInput++;
            arguments.AddRange(new[] { "-stream_loop", "-1", "-i", timeline.MusicPath });
        }

        var insertInputs = new List<int>();
        foreach (var insert in inserts)
        {
            if (insert.IsStill)
            {
                arguments.AddRange(new[]
                {
                    "-loop", "1",
                    "-framerate", timeline.Fps.ToString(CultureInfo.InvariantCulture),
                    "-t", insert.AuthoredDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                });
            }
            else if (insert.SourceOffsetSeconds > 0)
            {
                arguments.AddRange(new[] { "-ss", insert.SourceOffsetSeconds.ToString("0.###", CultureInfo.InvariantCulture) });
            }

            arguments.AddRange(new[] { "-i", insert.Source });
            insertInputs.Add(nextInput++);
        }

        var filter = BuildFilter(timeline, inserts, insertInputs, musicInput);

        _log?.Invoke($"Compositing {timeline.DurationSeconds:0.00}s at {timeline.Width}x{timeline.Height}");

        // Run from the subtitle directory so the filtergraph can reference it by bare filename,
        // which sidesteps every drive-letter and backslash escaping trap on Windows.
        var workingDirectory = string.IsNullOrWhiteSpace(timeline.SubtitlePath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(timeline.SubtitlePath));

        // A preview keeps the audio compressed too; the master keeps it uncompressed so the
        // delivery finalizer measures loudness from an unaltered program.
        var audioArguments = quality == EncodeQuality.Preview
            ? new[] { "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2" }
            : new[] { "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2" };

        await _ffmpeg.RunEncodeAsync(
            "timeline composition",
            quality,
            encoderArguments =>
            {
                var full = new List<string>(arguments)
                {
                    "-filter_complex", filter,
                    "-map", "[vout]",
                    "-map", "[aout]",
                    "-t", duration,
                };

                full.AddRange(encoderArguments);
                full.AddRange(new[]
                {
                    "-pix_fmt", "yuv420p",
                    "-r", timeline.Fps.ToString(CultureInfo.InvariantCulture),
                    "-fps_mode", "cfr",
                    "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
                });
                full.AddRange(audioArguments);
                full.AddRange(new[] { "-map_metadata", "-1", "-map_chapters", "-1", outputPath });
                return full;
            },
            cancellationToken,
            workingDirectory).ConfigureAwait(false);

        return outputPath;
    }

    internal string BuildFilter(
        RenderTimeline timeline,
        IReadOnlyList<TimelineClip> inserts,
        IReadOnlyList<int> insertInputs,
        int musicInput)
    {
        var filter = new StringBuilder();
        var width = timeline.Width;
        var height = timeline.Height;
        var fps = timeline.Fps;
        var portrait = height > width;

        // The plate is scaled to cover, then cropped by a time-varying window. Cropping a slightly
        // larger source is how the punch-in happens: a smaller crop window shown at the same output
        // size reads as a push in, and the source keeps its full detail throughout.
        // Framing and emphasis both crop into the same plate, so the oversample has to cover the
        // tightest combination of the two rather than either one alone.
        var punchScale = timeline.PunchIns.Count > 0
            ? timeline.PunchIns.Max(punch => punch.Scale)
            : 1.0;
        var shotScale = timeline.Shots.Count > 0
            ? timeline.Shots.Max(shot => shot.Scale)
            : 1.0;
        var combined = punchScale * shotScale;
        var oversampleWidth = EnsureEven((int)Math.Ceiling(width * combined));
        var oversampleHeight = EnsureEven((int)Math.Ceiling(height * combined));

        filter.Append(string.Format(
            CultureInfo.InvariantCulture,
            "[0:v]scale={0}:{1}:force_original_aspect_ratio=increase:flags=lanczos,crop={0}:{1},setsar=1,fps={2},format=yuv420p[over];",
            oversampleWidth,
            oversampleHeight,
            fps));

        filter.Append(string.Format(
            CultureInfo.InvariantCulture,
            "[over]crop=w='{0}':h='{1}':x='(iw-ow)/2':y='{2}',scale={3}:{4}:flags=bilinear,setsar=1[base];",
            BuildPunchExpression(timeline, oversampleWidth, width),
            BuildPunchExpression(timeline, oversampleHeight, height),
            BuildVerticalExpression(timeline),
            width,
            height));

        var current = "base";
        for (var index = 0; index < inserts.Count; index++)
        {
            var insert = inserts[index];
            var input = insertInputs[index];
            var label = $"ins{index}";

            // Inserts sit in the upper safe band on portrait and beside the presenter on landscape,
            // sized so they never reach the caption band or the frame edge.
            var insertWidth = EnsureEven((int)Math.Round(width * (portrait ? 0.84 : 0.40)));
            var insertHeight = EnsureEven((int)Math.Round(height * (portrait ? 0.30 : 0.42)));
            var x = portrait ? (width - insertWidth) / 2 : (int)Math.Round(width * 0.56);
            var y = portrait ? (int)Math.Round(height * 0.13) : (int)Math.Round(height * 0.16);

            var start = insert.AuthoredStartSeconds;
            var end = insert.AuthoredEndSeconds;
            var fade = Math.Min(0.35, insert.AuthoredDurationSeconds / 4);

            filter.Append(CultureInfo.InvariantCulture,
                $"[{input}:v]scale={insertWidth}:{insertHeight}:force_original_aspect_ratio=decrease:flags=lanczos," +
                $"pad={insertWidth}:{insertHeight}:(ow-iw)/2:(oh-ih)/2:color=0x101218,setsar=1,fps={fps}," +
                $"format=yuva420p,fade=t=in:st=0:d={fade.ToString("0.###", CultureInfo.InvariantCulture)}:alpha=1," +
                $"fade=t=out:st={(insert.AuthoredDurationSeconds - fade).ToString("0.###", CultureInfo.InvariantCulture)}:d={fade.ToString("0.###", CultureInfo.InvariantCulture)}:alpha=1," +
                $"setpts=PTS-STARTPTS+{start.ToString("0.###", CultureInfo.InvariantCulture)}/TB[{label}];");

            filter.Append(CultureInfo.InvariantCulture,
                $"[{current}][{label}]overlay={x}:{y}:enable='between(t,{start.ToString("0.###", CultureInfo.InvariantCulture)},{end.ToString("0.###", CultureInfo.InvariantCulture)})':eof_action=pass[ov{index}];");
            current = $"ov{index}";
        }

        if (timeline.ProgressBar)
        {
            var barHeight = Math.Max(4, (int)Math.Round(height * 0.0045));
            var accent = ToFfmpegColor(timeline.AccentColor);
            filter.Append(CultureInfo.InvariantCulture,
                $"[{current}]drawbox=x=0:y=ih-{barHeight}:w=iw:h={barHeight}:color=0x00000060:t=fill," +
                $"drawbox=x=0:y=ih-{barHeight}:w='iw*min(t/{timeline.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}\\,1)':h={barHeight}:color={accent}@0.92:t=fill[prog];");
            current = "prog";
        }

        if (!string.IsNullOrWhiteSpace(timeline.SubtitlePath) && _ffmpeg.Toolset.HasSubtitleBurnIn)
        {
            var fileName = Path.GetFileName(timeline.SubtitlePath);
            var subtitles = new StringBuilder($"subtitles={FfmpegService.EscapeFilterValue(fileName)}");
            if (!string.IsNullOrWhiteSpace(timeline.FontsDirectory) && Directory.Exists(timeline.FontsDirectory))
            {
                subtitles.Append(CultureInfo.InvariantCulture,
                    $":fontsdir={FfmpegService.EscapeFilterValue(timeline.FontsDirectory)}");
            }

            filter.Append(CultureInfo.InvariantCulture, $"[{current}]{subtitles}[subbed];");
            current = "subbed";
        }

        // A short lift from and to black keeps the opening and the tail from starting abruptly.
        var fadeOutStart = Math.Max(0, timeline.DurationSeconds - 0.45);
        filter.Append(CultureInfo.InvariantCulture,
            $"[{current}]fade=t=in:st=0:d=0.28,fade=t=out:st={fadeOutStart.ToString("0.###", CultureInfo.InvariantCulture)}:d=0.45,format=yuv420p[vout];");

        // Audio: narration is the program, any music sits well under it and ducks nothing else.
        filter.Append("[1:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,apad[narr];");

        if (musicInput >= 0)
        {
            var gain = timeline.MusicGainDb.ToString("0.##", CultureInfo.InvariantCulture);
            filter.Append(CultureInfo.InvariantCulture,
                $"[{musicInput}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo,volume={gain}dB[music];");
            filter.Append(CultureInfo.InvariantCulture,
                $"[narr][music]sidechaincompress=threshold=0.06:ratio=6:attack=12:release=320[ducked];");
            filter.Append("[narr][ducked]amix=inputs=2:normalize=0:duration=first[mixed];");
            filter.Append(CultureInfo.InvariantCulture,
                $"[mixed]atrim=0:{timeline.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS,afade=t=out:st={fadeOutStart.ToString("0.###", CultureInfo.InvariantCulture)}:d=0.45[aout]");
        }
        else
        {
            filter.Append(CultureInfo.InvariantCulture,
                $"[narr]atrim=0:{timeline.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS,afade=t=out:st={fadeOutStart.ToString("0.###", CultureInfo.InvariantCulture)}:d=0.45[aout]");
        }

        return filter.ToString();
    }

    /// <summary>
    /// Builds a crop dimension expression over time. The resting value is the chapter's framing,
    /// which steps at each cut; a punch-in eases in on top of that and back out again, using a
    /// raised cosine so neither the start nor the end of the move snaps.
    /// </summary>
    internal static string BuildPunchExpression(RenderTimeline timeline, int oversampled, int target)
    {
        if (oversampled <= target)
        {
            return oversampled.ToString(CultureInfo.InvariantCulture);
        }

        // Resting size, chapter by chapter. Innermost value first, then each shot wraps it.
        var expression = oversampled.ToString(CultureInfo.InvariantCulture);
        foreach (var shot in timeline.Shots.Where(shot => shot.Scale > 1.0))
        {
            var size = Clamp(oversampled / shot.Scale, target, oversampled);
            expression = string.Format(
                CultureInfo.InvariantCulture,
                "if(between(t,{0:0.###},{1:0.###}),{2},{3})",
                shot.StartSeconds,
                shot.EndSeconds,
                size,
                expression);
        }

        foreach (var punch in timeline.PunchIns)
        {
            var span = Math.Max(0.1, punch.DurationSeconds);

            // The punch eases from whatever the framing is resting at, so the two compose instead
            // of the emphasis snapping the frame back to a fixed size.
            var restingAt = RestingSize(timeline, punch.StartSeconds, oversampled, target);
            var tightest = Clamp(restingAt / Math.Max(1.0, punch.Scale), target, oversampled);
            var travel = restingAt - tightest;
            if (travel <= 0)
            {
                continue;
            }

            var eased = string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}*(0.5-0.5*cos(2*PI*(t-{2:0.###})/{3:0.###}))",
                restingAt,
                travel,
                punch.StartSeconds,
                span);

            expression = string.Format(
                CultureInfo.InvariantCulture,
                "if(between(t,{0:0.###},{1:0.###}),{2},{3})",
                punch.StartSeconds,
                punch.EndSeconds,
                eased,
                expression);
        }

        return expression;
    }

    /// <summary>
    /// Vertical placement of the crop window. A tighter framing sits higher so it lands on the
    /// face rather than the middle of the body.
    /// </summary>
    internal static string BuildVerticalExpression(RenderTimeline timeline)
    {
        const string centred = "(ih-oh)/2";
        var biased = timeline.Shots.Where(shot => Math.Abs(shot.YBias) > 0.001).ToList();
        if (biased.Count == 0)
        {
            return centred;
        }

        var expression = centred;
        foreach (var shot in biased)
        {
            // Bias runs -1..1 across the available travel, so it can never push past an edge.
            var offset = string.Format(
                CultureInfo.InvariantCulture,
                "(ih-oh)/2+(ih-oh)/2*{0:0.###}",
                Math.Clamp(shot.YBias, -1, 1));

            expression = string.Format(
                CultureInfo.InvariantCulture,
                "if(between(t,{0:0.###},{1:0.###}),{2},{3})",
                shot.StartSeconds,
                shot.EndSeconds,
                offset,
                expression);
        }

        return expression;
    }

    private static int RestingSize(RenderTimeline timeline, double time, int oversampled, int target)
    {
        var shot = timeline.Shots.LastOrDefault(candidate =>
            time >= candidate.StartSeconds && time <= candidate.EndSeconds && candidate.Scale > 1.0);

        return shot is null ? oversampled : Clamp(oversampled / shot.Scale, target, oversampled);
    }

    private static int Clamp(double value, int minimum, int maximum)
    {
        var rounded = (int)Math.Round(value);
        if (rounded % 2 != 0)
        {
            rounded++;
        }

        return Math.Clamp(rounded, minimum, maximum);
    }

    private static int EnsureEven(int value) => value % 2 == 0 ? value : value + 1;

    /// <summary>ffmpeg colours are 0xRRGGBB, unlike the &amp;HBBGGRR that libass wants.</summary>
    internal static string ToFfmpegColor(string hex)
    {
        var value = (hex ?? string.Empty).Trim().TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(character => new string(character, 2)));
        }

        if (value.Length != 6
            || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            value = "F4C430";
        }

        return "0x" + value.ToUpperInvariant();
    }
}
