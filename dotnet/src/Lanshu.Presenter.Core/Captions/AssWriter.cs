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
            foreach (var phrase in plan.Phrases)
            {
                WriteCaption(builder, phrase, options, accent);
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
        string accent)
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
            $"Dialogue: 1,{Time(phrase.StartSeconds)},{Time(phrase.EndSeconds)},Caption,,0,0,0,,{text}");
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
