using System.Globalization;
using System.Text;
using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Delivery;

public sealed record CardAttachment(string Path, double LeadSeconds, double TailSeconds)
{
    public double TotalSeconds => LeadSeconds + TailSeconds;

    public bool Attached => TotalSeconds > 0.001;
}

/// <summary>
/// Renders title and end cards and joins them onto the finished program.
///
/// Cards deliberately sit outside the edit. Captions, keyword callouts, punch-ins and chapter
/// times are all measured against the spoken narration, so pushing a card into the timeline
/// would shift every one of them by its length. Joining the card onto the rendered program
/// instead leaves the whole edit on its original clock, and the only thing that has to know
/// about the offset is the chapter list a publisher reads.
/// </summary>
public sealed class CardService
{
    private readonly FfmpegService _ffmpeg;
    private readonly Action<string>? _log;

    public CardService(FfmpegService ffmpeg, Action<string>? log = null)
    {
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public static bool AnyEnabled(JobCreative creative) =>
        IsUsable(creative.Intro) || IsUsable(creative.Outro);

    private static bool IsUsable(CardSettings card) =>
        card.Enabled
        && card.DurationSeconds > 0.1
        && (!string.IsNullOrWhiteSpace(card.Title)
            || !string.IsNullOrWhiteSpace(card.Subtitle)
            || !string.IsNullOrWhiteSpace(card.LogoPath));

    /// <summary>
    /// Joins any enabled cards onto <paramref name="programPath"/> and returns the joined file.
    /// A card that cannot be rendered is skipped rather than failing the run: a missing title
    /// card is a cosmetic loss, and losing the whole video over one is not a trade worth making.
    /// </summary>
    public async Task<CardAttachment> AttachAsync(
        JobPaths paths,
        JobManifest job,
        string programPath,
        CancellationToken cancellationToken = default)
    {
        if (!AnyEnabled(job.Creative))
        {
            return new CardAttachment(programPath, 0, 0);
        }

        var workingDirectory = Path.Combine(paths.Renders, "cards");
        Directory.CreateDirectory(workingDirectory);

        var pieces = new List<string>();
        var lead = 0.0;
        var tail = 0.0;

        if (IsUsable(job.Creative.Intro))
        {
            var intro = await TryRenderAsync(paths, job, job.Creative.Intro, workingDirectory, "intro", cancellationToken)
                .ConfigureAwait(false);
            if (intro is not null)
            {
                pieces.Add(intro);
                lead = job.Creative.Intro.DurationSeconds;
            }
        }

        pieces.Add(programPath);

        if (IsUsable(job.Creative.Outro))
        {
            var outro = await TryRenderAsync(paths, job, job.Creative.Outro, workingDirectory, "outro", cancellationToken)
                .ConfigureAwait(false);
            if (outro is not null)
            {
                pieces.Add(outro);
                tail = job.Creative.Outro.DurationSeconds;
            }
        }

        if (pieces.Count == 1)
        {
            return new CardAttachment(programPath, 0, 0);
        }

        var joined = Path.Combine(workingDirectory, "program-with-cards.mp4");
        FileSystemUtil.TryDelete(joined);

        try
        {
            await ConcatAsync(pieces, joined, job.Creative, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"Cards could not be joined onto the program, delivering without them: {exception.Message}");
            FileSystemUtil.TryDelete(joined);
            return new CardAttachment(programPath, 0, 0);
        }

        _log?.Invoke(
            $"Joined {(lead > 0 ? "an intro" : string.Empty)}"
            + (lead > 0 && tail > 0 ? " and " : string.Empty)
            + $"{(tail > 0 ? "an outro" : string.Empty)} card onto the program ({lead + tail:0.00}s added)");

        return new CardAttachment(joined, lead, tail);
    }

    private async Task<string?> TryRenderAsync(
        JobPaths paths,
        JobManifest job,
        CardSettings card,
        string workingDirectory,
        string stem,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RenderAsync(paths, job, card, workingDirectory, stem, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log?.Invoke($"The {stem} card could not be rendered and was skipped: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Renders one card. The text is drawn through libass rather than drawtext so the card uses
    /// the same font resolution, the same accent colour and the same CJK handling as the
    /// captions — a title card in a different typeface to the captions under it looks like a
    /// mistake.
    /// </summary>
    private async Task<string> RenderAsync(
        JobPaths paths,
        JobManifest job,
        CardSettings card,
        string workingDirectory,
        string stem,
        CancellationToken cancellationToken)
    {
        var creative = job.Creative;
        var duration = Math.Clamp(card.DurationSeconds, 0.4, 15);
        var output = Path.Combine(workingDirectory, stem + ".mp4");
        var assPath = Path.Combine(workingDirectory, stem + ".ass");
        FileSystemUtil.TryDelete(output);

        FileSystemUtil.WriteAtomic(assPath, BuildCardAss(card, creative, duration));

        var background = ToFfmpegColor(card.Background);
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-f", "lavfi",
            "-i", string.Create(CultureInfo.InvariantCulture,
                $"color=c={background}:s={creative.Width}x{creative.Height}:r={creative.Fps}:d={duration:0.###}"),
            "-f", "lavfi",
            "-i", string.Create(CultureInfo.InvariantCulture,
                $"anullsrc=channel_layout=stereo:sample_rate=48000:d={duration:0.###}"),
        };

        var filters = new List<string>();
        var video = "[0:v]";

        // ExpandPath rejects an empty string, and most cards have no logo at all.
        var logo = string.IsNullOrWhiteSpace(card.LogoPath)
            ? string.Empty
            : FileSystemUtil.ExpandPath(card.LogoPath);

        if (!string.IsNullOrEmpty(logo) && File.Exists(logo))
        {
            arguments.Add("-i");
            arguments.Add(logo);

            // The mark sits above the title and is never allowed to dominate the frame.
            var markWidth = (int)Math.Round(creative.Width * 0.34);
            var markY = (int)Math.Round(creative.Height * 0.30);
            filters.Add(string.Create(CultureInfo.InvariantCulture,
                $"[2:v]scale={markWidth}:-1:force_original_aspect_ratio=decrease[mark]"));
            filters.Add(string.Create(CultureInfo.InvariantCulture,
                $"[0:v][mark]overlay=x=(W-w)/2:y={markY}-h/2[bg]"));
            video = "[bg]";
        }

        var fontsDirectory = ResolveFontsDirectory();
        var subtitles = new StringBuilder("subtitles=");
        subtitles.Append(FfmpegService.EscapeFilterValue(assPath));
        if (!string.IsNullOrWhiteSpace(fontsDirectory))
        {
            subtitles.Append(":fontsdir=").Append(FfmpegService.EscapeFilterValue(fontsDirectory));
        }

        filters.Add($"{video}{subtitles},format=yuv420p[v]");

        arguments.Add("-filter_complex");
        arguments.Add(string.Join(';', filters));
        arguments.Add("-map");
        arguments.Add("[v]");
        arguments.Add("-map");
        arguments.Add("1:a");
        arguments.Add("-t");
        arguments.Add(duration.ToString("0.###", CultureInfo.InvariantCulture));
        arguments.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2" });
        arguments.AddRange(_ffmpeg.Encoder.Arguments(EncodeQuality.Intermediate));
        arguments.Add(output);

        await _ffmpeg.RunCheckedAsync($"render the {stem} card", arguments, cancellationToken).ConfigureAwait(false);

        if (FileSystemUtil.SafeLength(output) == 0)
        {
            throw new InvalidOperationException("the card encoded to an empty file");
        }

        return output;
    }

    /// <summary>
    /// Re-encodes the pieces into one program. A stream copy would be faster, but the card and
    /// the program come from different encoder invocations and only a real concat guarantees a
    /// continuous timestamp track that players seek correctly.
    /// </summary>
    private async Task ConcatAsync(
        IReadOnlyList<string> pieces,
        string output,
        JobCreative creative,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-y" };
        foreach (var piece in pieces)
        {
            arguments.Add("-i");
            arguments.Add(piece);
        }

        var graph = new StringBuilder();
        for (var index = 0; index < pieces.Count; index++)
        {
            graph.Append(CultureInfo.InvariantCulture,
                $"[{index}:v]scale={creative.Width}:{creative.Height},setsar=1,fps={creative.Fps},format=yuv420p[v{index}];");
            graph.Append(CultureInfo.InvariantCulture,
                $"[{index}:a]aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo[a{index}];");
        }

        for (var index = 0; index < pieces.Count; index++)
        {
            graph.Append(CultureInfo.InvariantCulture, $"[v{index}][a{index}]");
        }

        graph.Append(CultureInfo.InvariantCulture, $"concat=n={pieces.Count}:v=1:a=1[v][a]");

        arguments.Add("-filter_complex");
        arguments.Add(graph.ToString());
        arguments.AddRange(new[] { "-map", "[v]", "-map", "[a]" });
        arguments.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2" });
        arguments.AddRange(_ffmpeg.Encoder.Arguments(EncodeQuality.Intermediate));
        arguments.Add(output);

        await _ffmpeg.RunCheckedAsync("join the cards onto the program", arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Lays the card out as ASS. Title and subtitle are anchored to the frame centre rather than
    /// stacked from the top, so a card with no subtitle still reads as centred.
    /// </summary>
    internal static string BuildCardAss(CardSettings card, JobCreative creative, double duration)
    {
        var portrait = creative.Height > creative.Width;
        var titleSize = (int)Math.Round(creative.Height * (portrait ? 0.070 : 0.088));
        var subtitleSize = (int)Math.Round(titleSize * 0.42);
        var accent = AssWriter.ToAssColor(creative.AccentColor);
        var hasLogo = !string.IsNullOrWhiteSpace(card.LogoPath);

        // A logo already occupies the upper third, so the copy drops below the centre line.
        var anchorY = (int)Math.Round(creative.Height * (hasLogo ? 0.58 : 0.47));

        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]");
        builder.AppendLine("ScriptType: v4.00+");
        builder.AppendLine("WrapStyle: 0");
        builder.AppendLine("ScaledBorderAndShadow: yes");
        builder.AppendLine("YCbCr Matrix: TV.709");
        builder.AppendLine(CultureInfo.InvariantCulture, $"PlayResX: {creative.Width}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"PlayResY: {creative.Height}");
        builder.AppendLine();

        var font = string.IsNullOrWhiteSpace(creative.CaptionFont) ? "Sans" : creative.CaptionFont;
        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine(
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: CardTitle,{font},{titleSize},&H00FFFFFF,&H00FFFFFF,&H00101215,&H00000000,-1,0,0,0,100,100,1.0,0,1,0,0,5,40,40,40,1");
        builder.AppendLine();

        builder.AppendLine("[Events]");
        builder.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        // The copy settles a beat before the card ends so it is never mid-move at the cut.
        var settle = Math.Min(0.45, duration * 0.35);
        var fadeOut = (int)Math.Round(Math.Min(0.35, duration * 0.25) * 1000);

        // Title and subtitle go out as one centred block rather than two positioned events. A
        // title long enough to wrap is exactly when a subtitle placed a fixed distance below the
        // title's anchor lands on top of its second line; letting libass stack both inside one
        // \an5 event keeps the layout right at any title length.
        var copy = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(card.Title))
        {
            copy.Append(Escape(card.Title));
        }

        if (!string.IsNullOrWhiteSpace(card.Subtitle))
        {
            if (copy.Length > 0)
            {
                // A short blank line keeps the subtitle clear of the title's descenders.
                copy.Append(CultureInfo.InvariantCulture, $"\\N{{\\fs{(int)Math.Round(subtitleSize * 0.4)}}}\\N");
            }

            copy.Append(CultureInfo.InvariantCulture, $"{{\\fs{subtitleSize}\\c{accent[2..]}\\b0\\fsp3}}");
            copy.Append(Escape(card.Subtitle.ToUpperInvariant()));
        }

        if (copy.Length > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Dialogue: 1,{AssWriter.Time(0)},{AssWriter.Time(duration)},CardTitle,,0,0,0,,{{\\an5\\pos({creative.Width / 2},{anchorY})\\fad(220,{fadeOut})\\fscx96\\fscy96\\t(0,{(int)(settle * 1000)},\\fscx100\\fscy100)}}{copy}");
        }

        return builder.ToString();
    }

    private static string Escape(string text) => (text ?? string.Empty)
        .Replace("\\", "\\\\")
        .Replace("{", "\\{")
        .Replace("}", "\\}")
        .Replace("\r\n", "\\N")
        .Replace("\n", "\\N");

    /// <summary>ffmpeg's colour parser wants 0xRRGGBB, not the #RRGGBB people type.</summary>
    internal static string ToFfmpegColor(string hex)
    {
        var value = (hex ?? string.Empty).Trim().TrimStart('#');
        if (value.Length != 6 || !value.All(Uri.IsHexDigit))
        {
            return "0x0B0F14";
        }

        return "0x" + value.ToUpperInvariant();
    }

    private static string ResolveFontsDirectory()
    {
        var bundled = Path.Combine(ToolLocator.DataDirectory, "fonts");
        return Directory.Exists(bundled) ? bundled : string.Empty;
    }
}
