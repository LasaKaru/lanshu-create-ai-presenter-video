using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Presenter;
using Lanshu.Presenter.Core.Voice;

namespace Lanshu.Presenter.Core.Timeline;

/// <summary>
/// Assembles the deterministic timeline. The locked narration defines the duration and every
/// semantic boundary; presenter source time advances continuously so one take can cover the
/// whole video without visible resets.
/// </summary>
public sealed class TimelineBuilder
{
    private static readonly string[] StillExtensions =
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff" };

    private readonly FfmpegService _ffmpeg;

    public TimelineBuilder(FfmpegService ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    public async Task<RenderTimeline> BuildAsync(
        JobPaths paths,
        JobManifest job,
        ScriptDocument script,
        PresenterPlate plate,
        NarrationResult narration,
        string subtitlePath,
        CancellationToken cancellationToken = default,
        IReadOnlyList<KeywordCallout>? callouts = null,
        bool punchInsEnabled = true)
    {
        var creative = job.Creative;
        var timeline = new RenderTimeline
        {
            DurationSeconds = Math.Round(narration.DurationSeconds, 3),
            Width = creative.Width,
            Height = creative.Height,
            Fps = creative.Fps,
            NarrationPath = narration.AudioPath,
            MusicPath = creative.MusicPath,
            MusicGainDb = creative.MusicGainDb,
            SubtitlePath = subtitlePath,
            AccentColor = creative.AccentColor,
        };

        // One continuous presenter source, sliced by source time rather than regenerated per chapter.
        timeline.Clips.Add(new TimelineClip
        {
            Kind = "presenter",
            Source = plate.Path,
            AuthoredStartSeconds = 0,
            AuthoredDurationSeconds = timeline.DurationSeconds,
            SourceOffsetSeconds = 0,
            Label = plate.Provider,
        });

        var chapters = BuildChapters(script, narration);
        job.Plan.Chapters = chapters;
        job.Plan.OpeningTargetSeconds = chapters.FirstOrDefault()?.DurationSeconds ?? job.Plan.OpeningTargetSeconds;
        job.Plan.ClosingTargetSeconds = chapters.LastOrDefault()?.DurationSeconds ?? job.Plan.ClosingTargetSeconds;

        // Supporting media only appears where it proves or clarifies a spoken point, so it is
        // bound to body chapters and never to the hook or the close.
        var supporting = job.Input.SupportingMedia
            .Where(File.Exists)
            .ToList();

        if (supporting.Count > 0)
        {
            var bodyChapters = chapters
                .Where(chapter => chapter.Role == "beat")
                .ToList();

            if (bodyChapters.Count == 0)
            {
                bodyChapters = chapters.Skip(1).Take(Math.Max(0, chapters.Count - 2)).ToList();
            }

            for (var index = 0; index < supporting.Count && index < bodyChapters.Count; index++)
            {
                var media = supporting[index];
                var chapter = bodyChapters[index];
                var isStill = StillExtensions.Contains(Path.GetExtension(media).ToLowerInvariant());

                var available = chapter.DurationSeconds - 0.6;
                if (available < 1.2)
                {
                    continue;
                }

                var duration = Math.Min(available, 4.5);
                if (!isStill)
                {
                    var sourceDuration = await _ffmpeg.DurationAsync(media, cancellationToken).ConfigureAwait(false);
                    if (sourceDuration > 0.2)
                    {
                        duration = Math.Min(duration, sourceDuration);
                    }
                }

                timeline.Clips.Add(new TimelineClip
                {
                    Kind = "insert",
                    Source = media,
                    IsStill = isStill,
                    AuthoredStartSeconds = Math.Round(chapter.StartSeconds + 0.3, 3),
                    AuthoredDurationSeconds = Math.Round(duration, 3),
                    SourceOffsetSeconds = 0,
                    Label = chapter.Title,
                });

                chapter.SupportingMedia = paths.Relative(media);
            }
        }

        if (punchInsEnabled && callouts is { Count: > 0 })
        {
            timeline.PunchIns.AddRange(BuildPunchIns(callouts, timeline.DurationSeconds));
        }

        return timeline;
    }

    /// <summary>
    /// One push per keyword callout, starting a few frames before the word so the movement is
    /// already underway when it lands. Pushes that would overlap are dropped rather than stacked,
    /// because a frame that never settles reads as a wobble instead of emphasis.
    /// </summary>
    internal static IReadOnlyList<PunchIn> BuildPunchIns(
        IReadOnlyList<KeywordCallout> callouts,
        double programDuration)
    {
        const double lead = 0.08;
        const double minimumGap = 0.35;

        var punches = new List<PunchIn>();
        foreach (var callout in callouts.OrderBy(callout => callout.StartSeconds))
        {
            var start = Math.Max(0, callout.StartSeconds - lead);
            var duration = Math.Clamp(callout.EndSeconds - start, 0.8, 2.4);

            if (start + duration > programDuration)
            {
                duration = programDuration - start;
            }

            if (duration < 0.6)
            {
                continue;
            }

            if (punches.Count > 0 && start < punches[^1].EndSeconds + minimumGap)
            {
                continue;
            }

            punches.Add(new PunchIn
            {
                Label = callout.Text,
                StartSeconds = Math.Round(start, 3),
                DurationSeconds = Math.Round(duration, 3),
                Scale = 1.045,
            });
        }

        return punches;
    }

    /// <summary>Chapter boundaries come from the real measured narration, not from estimates.</summary>
    private static List<PlanChapter> BuildChapters(ScriptDocument script, NarrationResult narration)
    {
        var chapters = new List<PlanChapter>();

        for (var beatIndex = 0; beatIndex < script.Beats.Count; beatIndex++)
        {
            var segments = narration.Segments.Where(segment => segment.BeatIndex == beatIndex).ToList();
            if (segments.Count == 0)
            {
                continue;
            }

            var beat = script.Beats[beatIndex];
            var start = segments[0].StartSeconds;
            var end = segments[^1].EndSeconds;

            chapters.Add(new PlanChapter
            {
                Index = chapters.Count,
                Title = string.IsNullOrWhiteSpace(beat.Title) ? $"Chapter {chapters.Count + 1}" : beat.Title,
                Role = beat.Role,
                StartSeconds = Math.Round(start, 3),
                DurationSeconds = Math.Round(end - start, 3),
                Keyword = beat.Keyword,
            });
        }

        return chapters;
    }
}
