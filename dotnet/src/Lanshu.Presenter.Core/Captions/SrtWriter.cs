using System.Globalization;
using System.Text;

namespace Lanshu.Presenter.Core.Captions;

/// <summary>Sidecar captions for platforms that prefer an uploaded subtitle file.</summary>
public static class SrtWriter
{
    public static string Build(CaptionPlan plan)
    {
        var builder = new StringBuilder();
        var index = 0;

        foreach (var phrase in plan.Phrases)
        {
            if (string.IsNullOrWhiteSpace(phrase.Text))
            {
                continue;
            }

            index++;
            builder.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(Time(phrase.StartSeconds)).Append(" --> ").Append(Time(phrase.EndSeconds)).Append('\n');
            builder.Append(phrase.Text.Trim()).Append("\n\n");
        }

        return builder.ToString();
    }

    internal static string Time(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            (int)span.TotalHours,
            span.Minutes,
            span.Seconds,
            span.Milliseconds);
    }
}
