using System.Globalization;
using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Delivery;

/// <summary>
/// Port of scripts/finalize_delivery.sh. Preserves the input aspect ratio, applies two-pass
/// program loudness normalization, writes master and share encodes, fully decodes both, records
/// probes and a delivery report, and builds a nine-frame contact sheet for visual review.
/// </summary>
public sealed class FinalizeDeliveryService
{
    private readonly FfmpegService _ffmpeg;
    private readonly Action<string>? _log;

    public FinalizeDeliveryService(FfmpegService ffmpeg, Action<string>? log = null)
    {
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public sealed record Options
    {
        public double ProgramLufs { get; init; } = -16;

        public double TruePeak { get; init; } = -1.5;

        public double LoudnessRange { get; init; } = 9;

        /// <summary>Overwrite instead of refusing, used when re-exporting an existing job.</summary>
        public bool Overwrite { get; init; }
    }

    public async Task<DeliveryReport> RunAsync(
        string renderedPath,
        string outputDirectory,
        string stem,
        Options? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();

        if (!File.Exists(renderedPath) || FileSystemUtil.SafeLength(renderedPath) == 0)
        {
            throw new ArgumentException($"input render is missing or empty: {renderedPath}");
        }

        if (!FileSystemUtil.IsSafeStem(stem))
        {
            throw new ArgumentException($"output stem contains unsupported characters: {stem}");
        }

        Directory.CreateDirectory(outputDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        renderedPath = Path.GetFullPath(renderedPath);

        var master = Path.Combine(outputDirectory, $"{stem}-master.mp4");
        var share = Path.Combine(outputDirectory, $"{stem}-share.mp4");
        var reportPath = Path.Combine(outputDirectory, $"{stem}-delivery-report.json");
        var contact = Path.Combine(outputDirectory, $"{stem}-contact-sheet.png");
        var cover = Path.Combine(outputDirectory, $"{stem}-cover.png");

        foreach (var output in new[] { master, share, reportPath, contact, cover })
        {
            if (File.Exists(output))
            {
                if (!options.Overwrite)
                {
                    throw new IOException($"refusing to overwrite existing output: {output}");
                }

                FileSystemUtil.TryDelete(output);
            }
        }

        var sourceProbe = await _ffmpeg.ProbeAsync(renderedPath, cancellationToken).ConfigureAwait(false);
        if (!sourceProbe.HasVideo || !sourceProbe.HasAudio)
        {
            throw new InvalidOperationException("input must contain decodable video and audio streams");
        }

        var videoStream = sourceProbe.Video!;
        var width = videoStream.Width;
        var height = videoStream.Height;
        var fps = videoStream.FrameRate > 0 ? videoStream.FrameRate : 30;

        _log?.Invoke("Measuring program loudness");
        var measured = await _ffmpeg
            .MeasureLoudnessAsync(renderedPath, options.ProgramLufs, options.TruePeak, options.LoudnessRange, cancellationToken)
            .ConfigureAwait(false);

        var loudnorm = measured.ToLoudnormFilter(options.ProgramLufs, options.TruePeak, options.LoudnessRange);
        var videoFilter = string.Format(
            CultureInfo.InvariantCulture,
            "scale=trunc(iw/2)*2:trunc(ih/2)*2:flags=lanczos,setsar=1,fps={0:0.####},format=yuv420p",
            fps);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"lanshu-finalize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            _log?.Invoke("Encoding master");
            await EncodeAsync(renderedPath, master, Path.Combine(temporaryRoot, "master.mp4"),
                    crf: 16, preset: "slow", audioBitrate: "256k", videoFilter, loudnorm, cancellationToken)
                .ConfigureAwait(false);

            _log?.Invoke("Encoding share copy");
            await EncodeAsync(renderedPath, share, Path.Combine(temporaryRoot, "share.mp4"),
                    crf: 24, preset: "medium", audioBitrate: "160k", videoFilter, loudnorm, cancellationToken)
                .ConfigureAwait(false);

            _log?.Invoke("Verifying full decode");
            var masterDecoded = await _ffmpeg.FullDecodeAsync(master, true, cancellationToken).ConfigureAwait(false);
            var shareDecoded = await _ffmpeg.FullDecodeAsync(share, true, cancellationToken).ConfigureAwait(false);
            if (!masterDecoded || !shareDecoded)
            {
                throw new InvalidOperationException(
                    "a delivered file did not fully decode; the render is not acceptable");
            }

            var masterProbe = await _ffmpeg.ProbeAsync(master, cancellationToken).ConfigureAwait(false);
            var shareProbe = await _ffmpeg.ProbeAsync(share, cancellationToken).ConfigureAwait(false);
            var duration = masterProbe.DurationSeconds;

            var blackEvents = await _ffmpeg.CountBlackFrameEventsAsync(master, cancellationToken).ConfigureAwait(false);

            _log?.Invoke("Building contact sheet");
            await BuildContactSheetAsync(master, duration, width, height, contact, temporaryRoot, cancellationToken)
                .ConfigureAwait(false);

            // The time-zero frame is the deliberate cover frame from editing.md.
            await _ffmpeg
                .ExtractFrameAsync(master, 0.15, cover, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var report = new DeliveryReport
            {
                Input = Path.GetFileName(renderedPath),
                Master = Path.GetFileName(master),
                Share = Path.GetFileName(share),
                ContactSheet = Path.GetFileName(contact),
                CoverFrame = Path.GetFileName(cover),
                DurationSeconds = Math.Round(duration, 3),
                TargetProgramLufs = options.ProgramLufs,
                SourceLoudness = new JsonObject
                {
                    ["input_i"] = measured.InputI,
                    ["input_tp"] = measured.InputTp,
                    ["input_lra"] = measured.InputLra,
                    ["input_thresh"] = measured.InputThresh,
                    ["target_offset"] = measured.TargetOffset,
                },
                SourceProbe = sourceProbe.PortableRaw(),
                MasterProbe = masterProbe.PortableRaw(),
                ShareProbe = shareProbe.PortableRaw(),
                FullDecodePassed = true,
                BlackFrameEvents = blackEvents,
                MasterBytes = FileSystemUtil.SafeLength(master),
                ShareBytes = FileSystemUtil.SafeLength(share),
            };

            FileSystemUtil.WriteAtomic(reportPath, JobJson.Serialize(report));
            _log?.Invoke($"Master: {master}");
            _log?.Invoke($"Share: {share}");
            return report;
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(temporaryRoot);
        }
    }

    private async Task EncodeAsync(
        string input,
        string destination,
        string temporary,
        int crf,
        string preset,
        string audioBitrate,
        string videoFilter,
        string loudnorm,
        CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-stats", "-y",
            "-i", input,
            "-map", "0:v:0", "-map", "0:a:0", "-sn", "-dn",
            "-vf", videoFilter,
            "-af", loudnorm,
            "-c:v", "libx264", "-preset", preset, "-crf", crf.ToString(CultureInfo.InvariantCulture),
            "-profile:v", "high",
            "-pix_fmt", "yuv420p", "-tag:v", "avc1", "-fps_mode", "cfr",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
            "-c:a", "aac", "-b:a", audioBitrate, "-ar", "48000", "-ac", "2",
            "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart",
            temporary,
        };

        await _ffmpeg.RunCheckedAsync($"encoding {Path.GetFileName(destination)}", arguments, cancellationToken)
            .ConfigureAwait(false);

        if (FileSystemUtil.SafeLength(temporary) == 0)
        {
            throw new InvalidOperationException("the encoder produced an empty output");
        }

        File.Move(temporary, destination, overwrite: true);
    }

    /// <summary>Nine evenly spread frames covering the opening, chapters, emphasis beats and the close.</summary>
    private async Task BuildContactSheetAsync(
        string master,
        double duration,
        int width,
        int height,
        string destination,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        var timestamps = new[]
        {
            0.2,
            duration * 0.125,
            duration * 0.25,
            duration * 0.375,
            duration * 0.5,
            duration * 0.625,
            duration * 0.75,
            duration * 0.875,

            // Sample just before the closing fade so the final tile shows the last real frame
            // rather than the dip to black.
            Math.Max(0.2, duration - 0.6),
        };

        var frameFilter = width > height
            ? "scale=480:270:force_original_aspect_ratio=decrease:flags=lanczos,pad=480:270:(ow-iw)/2:(oh-ih)/2:color=black"
            : height > width
                ? "scale=270:480:force_original_aspect_ratio=decrease:flags=lanczos,pad=270:480:(ow-iw)/2:(oh-ih)/2:color=black"
                : "scale=360:360:force_original_aspect_ratio=decrease:flags=lanczos,pad=360:360:(ow-iw)/2:(oh-ih)/2:color=black";

        for (var index = 0; index < timestamps.Length; index++)
        {
            var frame = Path.Combine(temporaryRoot, $"frame-{index + 1:00}.png");
            await _ffmpeg
                .ExtractFrameAsync(master, Math.Max(0, timestamps[index]), frame, frameFilter, cancellationToken)
                .ConfigureAwait(false);
        }

        var tile = Path.Combine(temporaryRoot, "contact.png");
        var arguments = new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-framerate", "1", "-start_number", "1",
            "-i", Path.Combine(temporaryRoot, "frame-%02d.png"),
            "-frames:v", "1",
            "-vf", "tile=3x3:padding=12:margin=12:color=0x101218",
            "-update", "1",
            tile,
        };

        await _ffmpeg.RunCheckedAsync("contact sheet", arguments, cancellationToken).ConfigureAwait(false);
        File.Move(tile, destination, overwrite: true);
    }
}
