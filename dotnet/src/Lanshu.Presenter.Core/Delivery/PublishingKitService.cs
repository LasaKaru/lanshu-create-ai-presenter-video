using System.Globalization;
using System.Text;
using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Delivery;

public sealed record PublishingKit(
    IReadOnlyList<string> Thumbnails,
    string ChaptersPath,
    string DescriptionPath,
    IReadOnlyList<string> Notes);

/// <summary>
/// Everything a finished video needs on the way to a platform: thumbnail variants built from the
/// real cover frame, chapter markers from the measured chapter times, and a description that
/// matches them.
/// </summary>
public sealed class PublishingKitService
{
    /// <summary>YouTube ignores a chapter list unless the first starts at zero.</summary>
    private const int MinimumChapters = 3;

    /// <summary>YouTube also requires every chapter to run at least ten seconds.</summary>
    private const double MinimumChapterSeconds = 10;

    private readonly FfmpegService _ffmpeg;
    private readonly Action<string>? _log;

    public PublishingKitService(FfmpegService ffmpeg, Action<string>? log = null)
    {
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public async Task<PublishingKit> BuildAsync(
        JobPaths paths,
        JobManifest job,
        ScriptDocument script,
        string coverFramePath,
        string stem,
        CancellationToken cancellationToken = default,
        string? cleanSourceVideo = null,
        double programOffsetSeconds = 0)
    {
        var notes = new List<string>();
        var thumbnails = new List<string>();

        // The delivered cover frame already carries burned-in captions and a keyword callout, so
        // a headline drawn over it would be the third piece of text competing for the same frame.
        // The presenter plate has none of that, so it is the better base when it exists.
        var baseFrame = await ResolveThumbnailBaseAsync(paths, cleanSourceVideo, coverFramePath, cancellationToken)
            .ConfigureAwait(false);

        if (baseFrame is not null)
        {
            thumbnails.AddRange(await BuildThumbnailsAsync(
                    paths, job, script, baseFrame, stem, notes, cancellationToken)
                .ConfigureAwait(false));
        }
        else
        {
            notes.Add("no usable frame was available, so no thumbnails were generated");
        }

        var (chaptersPath, descriptionPath) =
            WriteChaptersAndDescription(paths, job, script, stem, notes, programOffsetSeconds);
        return new PublishingKit(thumbnails, chaptersPath, descriptionPath, notes);
    }

    /// <summary>
    /// Pulls a clean frame out of the presenter plate, a little way in so the opening fade is
    /// past. Falls back to the delivered cover frame when no plate is available.
    /// </summary>
    private async Task<string?> ResolveThumbnailBaseAsync(
        JobPaths paths,
        string? cleanSourceVideo,
        string coverFramePath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(cleanSourceVideo) && File.Exists(cleanSourceVideo))
        {
            try
            {
                var duration = await _ffmpeg.DurationAsync(cleanSourceVideo, cancellationToken).ConfigureAwait(false);
                var timestamp = duration > 1 ? duration * 0.15 : 0.1;

                var scratch = Path.Combine(paths.Temp, "thumbnails");
                Directory.CreateDirectory(scratch);
                var frame = Path.Combine(scratch, "base.png");
                FileSystemUtil.TryDelete(frame);

                await _ffmpeg
                    .ExtractFrameAsync(cleanSourceVideo, timestamp, frame, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                if (File.Exists(frame) && FileSystemUtil.SafeLength(frame) > 0)
                {
                    return frame;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log?.Invoke("Could not pull a clean thumbnail frame, using the cover: " + exception.Message);
            }
        }

        return File.Exists(coverFramePath) ? coverFramePath : null;
    }

    private async Task<IReadOnlyList<string>> BuildThumbnailsAsync(
        JobPaths paths,
        JobManifest job,
        ScriptDocument script,
        string coverFramePath,
        string stem,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        if (!_ffmpeg.Toolset.HasSubtitleBurnIn)
        {
            notes.Add("this FFmpeg build cannot burn in text, so the cover frame is delivered without a headline");
            return Array.Empty<string>();
        }

        var headline = ThumbnailHeadline(job, script);
        var results = new List<string>();
        var scratch = Path.Combine(paths.Temp, "thumbnails");
        Directory.CreateDirectory(scratch);

        var probe = await _ffmpeg.ProbeAsync(coverFramePath, cancellationToken).ConfigureAwait(false);
        var width = probe.Video?.Width ?? job.Creative.Width;
        var height = probe.Video?.Height ?? job.Creative.Height;

        foreach (var variant in ThumbnailVariant.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assPath = Path.Combine(scratch, $"{variant.Id}.ass");
            FileSystemUtil.WriteAllTextUtf8(
                assPath,
                variant.BuildAss(headline, width, height, job.Creative.AccentColor, TextUtil.IsCjk(headline)));

            var destination = Path.Combine(paths.Outputs, $"{stem}-thumb-{variant.Id}.png");
            FileSystemUtil.TryDelete(destination);

            var arguments = new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-i", coverFramePath,
                "-vf", $"subtitles={FfmpegService.EscapeFilterValue(Path.GetFileName(assPath))}",
                "-frames:v", "1",
                destination,
            };

            try
            {
                // Run from the subtitle directory so the filtergraph references it by bare name.
                await _ffmpeg
                    .RunCheckedAsync($"thumbnail {variant.Id}", arguments, cancellationToken, scratch)
                    .ConfigureAwait(false);
                results.Add(destination);
            }
            catch (Exception exception)
            {
                notes.Add($"thumbnail variant '{variant.Id}' failed: {exception.Message}");
            }
        }

        _log?.Invoke($"Wrote {results.Count} thumbnail variants");
        return results;
    }

    private static string ThumbnailHeadline(JobManifest job, ScriptDocument script)
    {
        var hook = script.Beats.FirstOrDefault(beat => beat.RoleValue == BeatRole.Hook);
        var candidate = hook?.Keyword;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = script.Title;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = job.Input.Topic;
        }

        return TextUtil.Truncate((candidate ?? "PRESENTER VIDEO").Trim(), 42);
    }

    /// <summary>
    /// Writes YouTube chapter markers and a description. Chapters are only emitted when they
    /// satisfy the platform's rules, because a list that breaks them is silently ignored and
    /// looks like a bug in the video rather than in the metadata.
    /// </summary>
    private (string ChaptersPath, string DescriptionPath) WriteChaptersAndDescription(
        JobPaths paths,
        JobManifest job,
        ScriptDocument script,
        string stem,
        List<string> notes,
        double programOffsetSeconds)
    {
        var chapters = job.Plan.Chapters.OrderBy(chapter => chapter.StartSeconds).ToList();
        var chaptersPath = Path.Combine(paths.Outputs, $"{stem}-chapters.txt");
        var descriptionPath = Path.Combine(paths.Outputs, $"{stem}-description.txt");

        var usable = chapters.Count >= MinimumChapters
                     && chapters.All(chapter => chapter.DurationSeconds >= MinimumChapterSeconds);

        var chapterLines = new StringBuilder();
        if (usable)
        {
            // The first marker must read 0:00 even if the first word lands a beat later. Chapter
            // times are measured against the narration, so an intro card joined onto the front of
            // the program pushes every later chapter back by its length — a marker that ignores
            // that lands the viewer a card's worth early on every chapter but the first.
            for (var index = 0; index < chapters.Count; index++)
            {
                var start = index == 0 ? 0 : chapters[index].StartSeconds + programOffsetSeconds;
                chapterLines.Append(Timecode(start)).Append(' ').AppendLine(ChapterTitle(chapters[index], index));
            }
        }
        else
        {
            var reason = chapters.Count < MinimumChapters
                ? $"only {chapters.Count} chapters; YouTube needs at least {MinimumChapters}"
                : $"a chapter is shorter than {MinimumChapterSeconds:0}s, which YouTube rejects";
            notes.Add($"chapter markers were not written: {reason}");
            chapterLines.AppendLine($"# Chapters were not written: {reason}.");
            chapterLines.AppendLine("# Listed below for reference only.");
            foreach (var (chapter, index) in chapters.Select((chapter, index) => (chapter, index)))
            {
                chapterLines.Append("# ").Append(Timecode(chapter.StartSeconds)).Append(' ')
                    .AppendLine(ChapterTitle(chapter, index));
            }
        }

        FileSystemUtil.WriteAllTextUtf8(chaptersPath, chapterLines.ToString());

        var description = new StringBuilder();
        description.AppendLine(string.IsNullOrWhiteSpace(script.Title) ? job.Input.Topic : script.Title);
        description.AppendLine();

        var synopsis = script.Beats.FirstOrDefault(beat => beat.RoleValue == BeatRole.Hook)?.Narration;
        if (!string.IsNullOrWhiteSpace(synopsis))
        {
            description.AppendLine(TextUtil.Truncate(synopsis.Trim(), 300));
            description.AppendLine();
        }

        if (usable)
        {
            description.AppendLine("Chapters");
            description.Append(chapterLines);
            description.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(job.Creative.Cta))
        {
            description.AppendLine(job.Creative.Cta.Trim());
            description.AppendLine();
        }

        FileSystemUtil.WriteAllTextUtf8(descriptionPath, description.ToString());
        return (chaptersPath, descriptionPath);
    }

    private static string ChapterTitle(PlanChapter chapter, int index)
    {
        var title = string.IsNullOrWhiteSpace(chapter.Title) ? chapter.Keyword : chapter.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"Part {index + 1}";
        }

        return TextUtil.Truncate(title.Trim(), 80);
    }

    internal static string Timecode(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(Math.Floor(seconds));
        return span.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", span.Minutes, span.Seconds);
    }
}

/// <summary>One thumbnail treatment, rendered by burning a small ASS file onto the cover frame.</summary>
internal sealed record ThumbnailVariant(string Id, string Description)
{
    public static readonly IReadOnlyList<ThumbnailVariant> All = new[]
    {
        new ThumbnailVariant("bar", "headline on an accent bar across the lower third"),
        new ThumbnailVariant("scrim", "headline over a dark scrim at the top"),
        new ThumbnailVariant("stamp", "oversized centred keyword"),
    };

    public string BuildAss(string headline, int width, int height, string accentColor, bool cjk)
    {
        var accent = AssWriter.ToAssColor(accentColor);
        var font = cjk
            ? (Environment.ToolLocator.IsWindows ? "Microsoft YaHei" : "Noto Sans CJK SC")
            : (Environment.ToolLocator.IsWindows ? "Segoe UI" : "DejaVu Sans");

        var size = Id switch
        {
            "stamp" => (int)Math.Round(height * 0.11),
            "scrim" => (int)Math.Round(height * 0.062),
            _ => (int)Math.Round(height * 0.058),
        };

        size = AssWriter.FitToWidth(headline, size, width, cjk);
        var text = AssWriter.Escape(headline.ToUpperInvariant());

        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]");
        builder.AppendLine("ScriptType: v4.00+");
        builder.AppendLine("WrapStyle: 0");
        builder.AppendLine("ScaledBorderAndShadow: yes");
        builder.AppendLine(CultureInfo.InvariantCulture, $"PlayResX: {width}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"PlayResY: {height}");
        builder.AppendLine();
        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine(
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: Head,{font},{size},&H00FFFFFF,&H00FFFFFF,&H00101215,&H00000000,-1,0,0,0,100,100,1,0,1,{Math.Max(3, size / 9)},0,5,20,20,20,1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: HeadDark,{font},{size},&H00101215,&H00FFFFFF,&H00101215,&H00000000,-1,0,0,0,100,100,1,0,1,0,0,5,20,20,20,1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: Plate,{font},{size},{accent},&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,5,0,0,0,1");
        builder.AppendLine();
        builder.AppendLine("[Events]");
        builder.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // A still frame only needs one long-lived event.
        const string window = "0:00:00.00,0:00:10.00";

        switch (Id)
        {
            case "bar":
            {
                var barHeight = (int)Math.Round(size * 1.7);
                var centreY = (int)Math.Round(height * 0.78);
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 0,{window},Plate,,0,0,0,,{Rect(0, centreY - (barHeight / 2), width, barHeight)}");
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 1,{window},HeadDark,,0,0,0,,{{\\pos({width / 2},{centreY})}}{text}");
                break;
            }

            case "scrim":
            {
                var scrimHeight = (int)Math.Round(height * 0.26);
                // Anchored with \an7 like every other drawing: libass places a vector shape
                // by its own origin, so with no explicit top-left it lands frame-centre.
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 0,{window},Plate,,0,0,0,,{Rect(0, 0, width, scrimHeight, @"\1c&H101215&\alpha&H40&")}");
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 1,{window},Head,,0,0,0,,{{\\pos({width / 2},{scrimHeight / 2})}}{text}");
                break;
            }

            default:
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 1,{window},Head,,0,0,0,,{{\\pos({width / 2},{height / 2})\\c{accent[2..]}\\bord{Math.Max(5, size / 7)}\\3c&H101215&}}{text}");
                break;
            }
        }

        return builder.ToString();
    }

    /// <summary>An ASS drawing anchored at its top-left, matching how libass places vector shapes.</summary>
    private static string Rect(int left, int top, int width, int height, string extraTags = "") =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\\an7\\pos({0},{1}){4}\\p1}}m 0 0 l {2} 0 l {2} {3} l 0 {3}{{\\p0}}",
            left,
            top,
            width,
            height,
            extraTags);
}
