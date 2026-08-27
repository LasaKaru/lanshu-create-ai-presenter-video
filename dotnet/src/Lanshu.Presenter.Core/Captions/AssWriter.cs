using System.Globalization;
using System.Text;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Captions;

public sealed record CaptionStyleOptions
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public string FontName { get; init; } = "";

    public string AccentColor { get; init; } = "#F4C430";

    public string Watermark { get; init; } = "";

    public bool CaptionsEnabled { get; init; } = true;

    public bool CalloutsEnabled { get; init; } = true;

    /// <summary>classic | karaoke | boxed | tiktok</summary>
    public string CaptionStyle { get; init; } = "classic";

    public bool Cjk { get; init; }

    public bool Portrait => Height > Width;

    public CaptionLayout Layout => new(Width, Height);
}

/// <summary>
/// Writes an Advanced SubStation file that libass burns in: captions plus the rotating family of
/// presenter-side keyword presets described in editing.md. Everything is time-bound to real
/// spoken anchors, and every element stays inside a fixed safe region away from the face.
/// </summary>
public static class AssWriter
{
    public static readonly string[] PresetNames =
    {
        "radial_burst",
        "tilted_ribbon",
        "marker_circle",
        "type_contrast",
        "word_chips",
        "double_outline",
    };

    public static string Build(CaptionPlan plan, CaptionStyleOptions options)
    {
        var builder = new StringBuilder();
        var font = ResolveFont(options);
        var captionSize = options.Layout.FontSizePixels;
        var calloutSize = (int)Math.Round(options.Height * (options.Portrait ? 0.062 : 0.075));
        var kickerSize = (int)Math.Round(calloutSize * 0.34);
        var marginV = (int)Math.Round(options.Height * (options.Portrait ? 0.145 : 0.075));
        var marginH = (int)Math.Round(options.Width * options.Layout.SideMargin);
        var accent = ToAssColor(options.AccentColor);

        builder.AppendLine("[Script Info]");
        builder.AppendLine("ScriptType: v4.00+");
        builder.AppendLine("WrapStyle: 0");
        builder.AppendLine("ScaledBorderAndShadow: yes");
        builder.AppendLine("YCbCr Matrix: TV.709");
        builder.AppendLine(CultureInfo.InvariantCulture, $"PlayResX: {options.Width}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"PlayResY: {options.Height}");
        builder.AppendLine();

        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine(
            "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: Caption,{font},{captionSize},&H00FFFFFF,&H00FFFFFF,&H00101215,&H96000000,-1,0,0,0,100,100,0.6,0,1,{Math.Max(2, captionSize / 12)},0,2,{marginH},{marginH},{marginV},1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: CaptionAccent,{font},{captionSize},{accent},&H00FFFFFF,&H00101215,&H96000000,-1,0,0,0,100,100,0.6,0,1,{Math.Max(2, captionSize / 12)},0,2,{marginH},{marginH},{marginV},1");
        // A boxed caption paints a plate behind the text so it stays readable over a busy frame.
        // BorderStyle 3 draws that plate in OutlineColour, and ASS alpha runs backwards — &H00 is
        // opaque and &HFF is invisible — so the near-solid plate wants a *low* alpha byte.
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: CaptionBoxed,{font},{captionSize},&H00FFFFFF,&H00FFFFFF,&H14101215,&H14101215,-1,0,0,0,100,100,0.6,0,3,{Math.Max(6, captionSize / 5)},0,2,{marginH},{marginH},{marginV},1");
        // Karaoke sweeps PrimaryColour over SecondaryColour, so the accent must be primary here.
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: CaptionKaraoke,{font},{captionSize},{accent},&H00FFFFFF,&H00101215,&H96000000,-1,0,0,0,100,100,0.6,0,1,{Math.Max(2, captionSize / 12)},0,2,{marginH},{marginH},{marginV},1");
        // One or two words at a time, sitting higher and much larger than a reading caption.
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: CaptionPop,{font},{(int)Math.Round(captionSize * 1.55)},&H00FFFFFF,&H00FFFFFF,&H00101215,&H00000000,-1,0,0,0,100,100,0.4,0,1,{Math.Max(4, captionSize / 6)},0,2,{marginH / 2},{marginH / 2},{(int)Math.Round(marginV * 1.35)},1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: Callout,{font},{calloutSize},&H00FFFFFF,&H00FFFFFF,&H00101215,&H00000000,-1,0,0,0,100,100,0.8,0,1,{Math.Max(3, calloutSize / 10)},0,5,20,20,20,1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: CalloutAccent,{font},{calloutSize},{accent},&H00FFFFFF,&H00101215,&H00000000,-1,0,0,0,100,100,0.8,0,1,{Math.Max(3, calloutSize / 10)},0,5,20,20,20,1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: Kicker,{font},{kickerSize},{accent},&H00FFFFFF,&H00101215,&H00000000,-1,0,0,0,100,100,4.0,0,1,2,0,5,20,20,20,1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: Shape,{font},{calloutSize},{accent},&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,5,0,0,0,1");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Style: Watermark,{font},{Math.Max(16, (int)Math.Round(options.Height * 0.019))},&H50FFFFFF,&H00FFFFFF,&H00101215,&H00000000,0,0,0,0,100,100,1.2,0,1,1,0,9,{marginH / 2},{marginH / 2},{marginH / 3},1");
        builder.AppendLine();

        builder.AppendLine("[Events]");
        builder.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        if (options.CaptionsEnabled)
        {
            // Karaoke needs to know when each word lands. On estimated timings the sweep drifts
            // away from the voice within a sentence, which reads worse than no sweep at all, so
            // the style quietly falls back rather than shipping a wrong-looking effect.
            var style = options.CaptionStyle?.Trim().ToLowerInvariant() ?? "classic";
            if (style is "karaoke" && !plan.WordTimingsAreMeasured)
            {
                style = "classic";
            }

            foreach (var phrase in plan.Phrases)
            {
                switch (style)
                {
                    case "karaoke":
                        WriteKaraokeCaption(builder, phrase);
                        break;
                    case "boxed":
                        WriteCaption(builder, phrase, options, accent, "CaptionBoxed");
                        break;
                    case "tiktok":
                        WritePopCaption(builder, phrase, accent);
                        break;
                    default:
                        WriteCaption(builder, phrase, options, accent, "Caption");
                        break;
                }
            }
        }

        if (options.CalloutsEnabled)
        {
            foreach (var callout in plan.Callouts)
            {
                WriteCallout(builder, callout, options, calloutSize, kickerSize);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Watermark))
        {
            var end = Math.Max(
                plan.Phrases.Count > 0 ? plan.Phrases[^1].EndSeconds : 0,
                plan.Callouts.Count > 0 ? plan.Callouts[^1].EndSeconds : 0) + 3600;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Dialogue: 0,{Time(0)},{Time(end)},Watermark,,0,0,0,,{Escape(options.Watermark)}");
        }

        return builder.ToString();
    }

    private static void WriteCaption(
        StringBuilder builder,
        CaptionPhrase phrase,
        CaptionStyleOptions options,
        string accent,
        string styleName)
    {
        // Quick entrance and exit so reading stability is preserved between phrases.
        var text = new StringBuilder("{\\fad(90,90)}");

        if (!string.IsNullOrWhiteSpace(phrase.Highlight)
            && phrase.Text.Contains(phrase.Highlight, StringComparison.OrdinalIgnoreCase))
        {
            var index = phrase.Text.IndexOf(phrase.Highlight, StringComparison.OrdinalIgnoreCase);
            var before = phrase.Text[..index];
            var word = phrase.Text.Substring(index, phrase.Highlight.Length);
            var after = phrase.Text[(index + phrase.Highlight.Length)..];
            text.Append(Escape(before));
            text.Append(CultureInfo.InvariantCulture, $"{{\\c{accent[2..]}}}");
            text.Append(Escape(word));
            text.Append("{\\c&HFFFFFF&}");
            text.Append(Escape(after));
        }
        else
        {
            text.Append(Escape(phrase.Text));
        }

        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Dialogue: 1,{Time(phrase.StartSeconds)},{Time(phrase.EndSeconds)},{styleName},,0,0,0,,{text}");
    }

    /// <summary>
    /// Sweeps the accent across the phrase in time with the voice. libass measures \kf in
    /// centiseconds of its own event, so the durations must tile the event exactly: any gap
    /// between two measured words is charged to the word that follows it, and the leftover after
    /// the last word is charged to that word, so the sweep finishes exactly when the line does.
    /// </summary>
    private static void WriteKaraokeCaption(StringBuilder builder, CaptionPhrase phrase)
    {
        if (phrase.Words.Count == 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Dialogue: 1,{Time(phrase.StartSeconds)},{Time(phrase.EndSeconds)},CaptionKaraoke,,0,0,0,,{{\fad(90,90)}}{Escape(phrase.Text)}");
            return;
        }

        var text = new StringBuilder("{\fad(90,90)}");
        var cursor = phrase.StartSeconds;

        for (var index = 0; index < phrase.Words.Count; index++)
        {
            var word = phrase.Words[index];
            var isLast = index == phrase.Words.Count - 1;
            var until = isLast ? phrase.EndSeconds : Math.Max(word.EndSeconds, cursor);
            var centiseconds = (int)Math.Round(Math.Max(0, until - cursor) * 100);
            cursor = until;

            text.Append(CultureInfo.InvariantCulture, $"{{\\kf{centiseconds}}}");
            text.Append(Escape(word.Word));
            if (!isLast)
            {
                text.Append(' ');
            }
        }

        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Dialogue: 1,{Time(phrase.StartSeconds)},{Time(phrase.EndSeconds)},CaptionKaraoke,,0,0,0,,{text}");
    }

    /// <summary>
    /// One or two words at a time, each landing with a short scale pop. Without measured word
    /// timings the phrase is divided evenly, which still reads correctly because each chunk is
    /// on screen only as long as its share of the phrase.
    /// </summary>
    private static void WritePopCaption(StringBuilder builder, CaptionPhrase phrase, string accent)
    {
        var chunks = ChunkForPop(phrase);
        foreach (var (text, start, end) in chunks)
        {
            if (end - start < 0.06)
            {
                continue;
            }

            var colour = string.Equals(text, phrase.Highlight, StringComparison.OrdinalIgnoreCase)
                ? $"\\c{accent[2..]}"
                : string.Empty;

            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Dialogue: 1,{Time(start)},{Time(end)},CaptionPop,,0,0,0,,{{{colour}\\fscx86\\fscy86\\t(0,110,\\fscx104\\fscy104)\\t(110,180,\\fscx100\\fscy100)\\fad(40,60)}}{Escape(text.ToUpperInvariant())}");
        }
    }

    /// <summary>Groups a phrase into pop-sized chunks of at most two words, with their own times.</summary>
    internal static IReadOnlyList<(string Text, double Start, double End)> ChunkForPop(CaptionPhrase phrase)
    {
        const int wordsPerChunk = 2;
        var chunks = new List<(string, double, double)>();

        if (phrase.Words.Count == 0)
        {
            var words = phrase.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return chunks;
            }

            var groups = (int)Math.Ceiling(words.Length / (double)wordsPerChunk);
            var span = phrase.DurationSeconds / Math.Max(1, groups);
            for (var index = 0; index < groups; index++)
            {
                var slice = words.Skip(index * wordsPerChunk).Take(wordsPerChunk);
                var start = phrase.StartSeconds + (index * span);
                var end = index == groups - 1 ? phrase.EndSeconds : start + span;
                chunks.Add((string.Join(' ', slice), start, end));
            }

            return chunks;
        }

        for (var index = 0; index < phrase.Words.Count; index += wordsPerChunk)
        {
            var slice = phrase.Words.Skip(index).Take(wordsPerChunk).ToList();
            var isLast = index + wordsPerChunk >= phrase.Words.Count;
            var start = index == 0 ? phrase.StartSeconds : slice[0].StartSeconds;
            var end = isLast ? phrase.EndSeconds : slice[^1].EndSeconds;
            chunks.Add((string.Join(' ', slice.Select(word => word.Word)), start, end));
        }

        return chunks;
    }

    private static void WriteCallout(
        StringBuilder builder,
        KeywordCallout callout,
        CaptionStyleOptions options,
        int calloutSize,
        int kickerSize)
    {
        // Keep every callout inside one consistent region that never crosses the face or captions.
        var x = options.Portrait ? options.Width / 2 : (int)Math.Round(options.Width * 0.70);
        var y = options.Portrait
            ? (int)Math.Round(options.Height * 0.615)
            : (int)Math.Round(options.Height * 0.30);

        var start = callout.StartSeconds;
        var end = callout.EndSeconds;
        var word = Escape(callout.Text.ToUpperInvariant());
        var kicker = Escape(callout.Kicker.ToUpperInvariant());
        var accent = ToAssColor(options.AccentColor);

        // A long keyword must shrink rather than run off the frame.
        calloutSize = FitToWidth(callout.Text, calloutSize, options.Width, options.Cjk);
        kickerSize = Math.Max(14, (int)Math.Round(calloutSize * 0.34));

        switch (callout.Preset)
        {
            case "radial_burst":
            {
                var radius = (int)(calloutSize * 2.6);
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 2,{Time(start)},{Time(end)},Shape,,0,0,0,,{Shape(x, y, radius * 2, radius * 2, Rays(radius), $"\\alpha&H30&\\fad(80,140)\\org({x},{y})\\frz0\\t(0,900,\\frz14)")}");
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 3,{Time(start + 0.05)},{Time(end)},CalloutAccent,,0,0,0,,{{\\pos({x},{y})\\fscx60\\fscy60\\t(0,170,\\fscx104\\fscy104)\\t(170,240,\\fscx100\\fscy100)\\fad(60,120)}}{word}");
                break;
            }

            case "tilted_ribbon":
            {
                var barWidth = (int)(calloutSize * (1.4 + (callout.Text.Length * 0.42)));
                var barHeight = (int)(calloutSize * 1.45);
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 2,{Time(start)},{Time(end)},Shape,,0,0,0,,{Shape(x, y, barWidth, barHeight, Bar(barWidth, barHeight), $"\\org({x},{y})\\frz-7\\fad(70,120)\\fscx0\\fscy100\\t(0,220,\\fscx100)")}");
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 3,{Time(start + 0.14)},{Time(end)},Callout,,0,0,0,,{{\\pos({x},{y})\\frz-7\\c&H101215&\\3a&HFF&\\fad(60,120)}}{word}");
                break;
            }

            case "marker_circle":
            {
                var ringWidth = (int)(calloutSize * (1.9 + (callout.Text.Length * 0.46)));
                var ringHeight = (int)(calloutSize * 1.9);
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 3,{Time(start)},{Time(end)},Callout,,0,0,0,,{{\\pos({x},{y})\\fad(70,120)}}{word}");
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 2,{Time(start + 0.16)},{Time(end)},Shape,,0,0,0,,{Shape(x, y, ringWidth, ringHeight, Ellipse(ringWidth, ringHeight), $"\\1a&HFF&\\3a&H10&\\3c{accent[2..]}\\bord{Math.Max(5, calloutSize / 12)}\\org({x},{y})\\frz-4\\fad(50,120)\\t(0,420,\\frz3)")}");
                break;
            }

            case "type_contrast":
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 2,{Time(start)},{Time(end)},Kicker,,0,0,0,,{{\\pos({x},{y - (int)(calloutSize * 0.85)})\\fad(60,110)\\fsp2\\t(0,240,\\fsp8)}}{kicker}");
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 3,{Time(start + 0.08)},{Time(end)},CalloutAccent,,0,0,0,,{{\\pos({x},{y})\\fs{(int)(calloutSize * 1.3)}\\fscy70\\t(0,180,\\fscy104)\\t(180,250,\\fscy100)\\fad(60,110)}}{word}");
                break;

            case "word_chips":
                WriteWordChips(builder, callout, x, y, calloutSize);
                break;

            default:
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 2,{Time(start)},{Time(end)},CalloutAccent,,0,0,0,,{{\\pos({x + 7},{y + 7})\\bord{Math.Max(4, calloutSize / 7)}\\3c&H101215&\\alpha&H60&\\fad(60,120)}}{word}");
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"Dialogue: 3,{Time(start + 0.07)},{Time(end)},Callout,,0,0,0,,{{\\pos({x},{y})\\bord{Math.Max(3, calloutSize / 10)}\\fad(60,120)\\fscx92\\t(0,190,\\fscx100)}}{word}");
                break;
        }
    }

    /// <summary>
    /// Shrinks the callout font until the word fits the safe width. Bold capitals average a
    /// little under two thirds of the nominal size per glyph; a CJK glyph fills it.
    /// </summary>
    internal static int FitToWidth(string text, int nominalSize, int frameWidth, bool cjk)
    {
        var length = Math.Max(1, text.Trim().Length);
        var glyphRatio = cjk ? 1.0 : 0.62;
        var available = frameWidth * 0.86;
        var needed = length * nominalSize * glyphRatio;
        if (needed <= available)
        {
            return nominalSize;
        }

        return Math.Max(20, (int)Math.Floor(available / (length * glyphRatio)));
    }

    /// <summary>Staggers each word by a few frames so the cluster assembles instead of popping.</summary>
    private static void WriteWordChips(
        StringBuilder builder,
        KeywordCallout callout,
        int x,
        int y,
        int calloutSize)
    {
        var words = callout.Text
            .Split(new[] { ' ', '\u3000' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (words.Count == 0)
        {
            words.Add(callout.Text);
        }

        var step = (int)Math.Round(calloutSize * 1.2);
        var top = y - ((words.Count - 1) * step / 2);

        for (var index = 0; index < words.Count; index++)
        {
            var delay = index * 0.085;
            var chipY = top + (index * step);
            var style = index % 2 == 0 ? "CalloutAccent" : "Callout";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"Dialogue: 3,{Time(callout.StartSeconds + delay)},{Time(callout.EndSeconds)},{style},,0,0,0,,{{\\pos({x},{chipY})\\fs{(int)(calloutSize * 0.8)}\\fad(70,110)\\fscx104\\fscy104\\t(0,170,\\fscx100\\fscy100)}}{Escape(words[index].ToUpperInvariant())}");
        }
    }

    /// <summary>
    /// Emits one vector-drawing event. libass positions a drawing by its own coordinate origin,
    /// not by its bounding box, so every path is authored inside a 0..width by 0..height box and
    /// anchored with \an7 at the box's top-left corner. Rotation uses \org so it still turns
    /// around the callout's centre.
    /// </summary>
    private static string Shape(int centreX, int centreY, int width, int height, string path, string tags)
    {
        var left = centreX - (width / 2);
        var top = centreY - (height / 2);
        return $"{{\\an7\\pos({left},{top}){tags}\\p1}}{path}{{\\p0}}";
    }

    /// <summary>Eight tapered rays inside a 2R box, radiating from its centre.</summary>
    private static string Rays(int radius)
    {
        var builder = new StringBuilder();
        var inner = Math.Max(8, radius / 3);
        for (var index = 0; index < 8; index++)
        {
            var angle = index * Math.PI / 4;
            const double spread = 0.085;
            var x1 = radius + (inner * Math.Cos(angle - spread));
            var y1 = radius + (inner * Math.Sin(angle - spread));
            var x2 = radius + (radius * Math.Cos(angle));
            var y2 = radius + (radius * Math.Sin(angle));
            var x3 = radius + (inner * Math.Cos(angle + spread));
            var y3 = radius + (inner * Math.Sin(angle + spread));
            builder.Append(CultureInfo.InvariantCulture, $"m {x1:0} {y1:0} l {x2:0} {y2:0} l {x3:0} {y3:0} ");
        }

        return builder.ToString().Trim();
    }

    private static string Bar(int width, int height) =>
        string.Format(CultureInfo.InvariantCulture, "m 0 0 l {0} 0 l {0} {1} l 0 {1}", width, height);

    /// <summary>An ellipse from four bezier arcs, authored inside a 0..w by 0..h box.</summary>
    private static string Ellipse(int width, int height)
    {
        var a = width / 2.0;
        var b = height / 2.0;
        var kx = a * 0.5523;
        var ky = b * 0.5523;
        return string.Format(
            CultureInfo.InvariantCulture,
            "m 0 {1:0} b 0 {2:0} {3:0} 0 {0:0} 0 b {4:0} 0 {5:0} {2:0} {5:0} {1:0} b {5:0} {6:0} {4:0} {7:0} {0:0} {7:0} b {3:0} {7:0} 0 {6:0} 0 {1:0}",
            a,
            b,
            b - ky,
            a - kx,
            a + kx,
            width,
            b + ky,
            height);
    }

    private static string ResolveFont(CaptionStyleOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FontName))
        {
            return options.FontName;
        }

        if (options.Cjk)
        {
            return ToolLocator.IsWindows ? "Microsoft YaHei" : "Noto Sans CJK SC";
        }

        return ToolLocator.IsWindows ? "Segoe UI" : "DejaVu Sans";
    }

    /// <summary>ASS colours are &amp;HAABBGGRR; the input is a familiar #RRGGBB.</summary>
    internal static string ToAssColor(string hex)
    {
        var value = (hex ?? string.Empty).Trim().TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(character => new string(character, 2)));
        }

        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            value = "F4C430";
        }

        var red = value[..2];
        var green = value.Substring(2, 2);
        var blue = value.Substring(4, 2);
        return $"&H00{blue}{green}{red}".ToUpperInvariant();
    }

    internal static string Time(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var hours = (int)(seconds / 3600);
        var minutes = (int)(seconds % 3600 / 60);
        var wholeSeconds = (int)(seconds % 60);
        var centiseconds = (int)Math.Round((seconds - Math.Floor(seconds)) * 100);
        if (centiseconds >= 100)
        {
            centiseconds = 99;
        }

        return $"{hours}:{minutes:00}:{wholeSeconds:00}.{centiseconds:00}";
    }

    /// <summary>Braces start an override block in ASS, so user text must never contain them raw.</summary>
    internal static string Escape(string text) =>
        (text ?? string.Empty)
        .Replace("\\", "\\​")
        .Replace("{", "(")
        .Replace("}", ")")
        .Replace("\r\n", "\\N")
        .Replace("\n", "\\N");
}
