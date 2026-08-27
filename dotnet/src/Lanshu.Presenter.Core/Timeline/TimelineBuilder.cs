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

    /// <summary>How far into a chapter an insert lands when nobody has said otherwise.</summary>
    private const double DefaultInsertLeadSeconds = 0.3;

    /// <summary>An insert that outstays this stops supporting the point and becomes the point.</summary>
    private const double MaximumInsertSeconds = 4.5;

    private readonly record struct Placement(
        PlanChapter Chapter,
        string Media,
        double OffsetSeconds,
        double DurationSeconds);

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
        bool punchInsEnabled = true,
        bool multiShotEnabled = false)
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

        // Supporting media only appears where it proves or clarifies a spoken point. Where it
        // lands is ResolvePlacements' call: an operator's own assignments if there are any,
        // otherwise a round-robin over the body chapters.
        var supporting = job.Input.SupportingMedia
            .Where(File.Exists)
            .ToList();

        if (supporting.Count > 0)
        {
            foreach (var placement in ResolvePlacements(job, chapters, supporting))
            {
                var (chapter, media, requestedOffset, requestedDuration) = placement;
                var isStill = StillExtensions.Contains(Path.GetExtension(media).ToLowerInvariant());

                var lead = requestedOffset >= 0 ? requestedOffset : DefaultInsertLeadSeconds;
                var available = chapter.DurationSeconds - lead - 0.3;
                if (available < 1.2)
                {
                    continue;
                }

                var duration = requestedDuration > 0
                    ? Math.Min(requestedDuration, available)
                    : Math.Min(available, MaximumInsertSeconds);

                if (!isStill)
                {
                    var sourceDuration = await _ffmpeg.DurationAsync(media, cancellationToken).ConfigureAwait(false);
                    if (sourceDuration > 0.2)
                    {
                        duration = Math.Min(duration, sourceDuration);
                    }
                }

                if (duration < 0.6)
                {
                    continue;
                }

                timeline.Clips.Add(new TimelineClip
                {
                    Kind = "insert",
                    Source = media,
                    IsStill = isStill,
                    AuthoredStartSeconds = Math.Round(chapter.StartSeconds + lead, 3),
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

        if (multiShotEnabled)
        {
            timeline.Shots.AddRange(BuildShots(chapters, timeline.DurationSeconds));
        }

        return timeline;
    }

    /// <summary>
    /// Decides where each supporting file goes. Hand-written assignments win outright — including
    /// an assignment with no media, which is how an operator says "leave this chapter clean" and
    /// has that decision survive the next run. With no assignments at all the old round-robin
    /// applies, bound to body chapters so media never lands on the hook or the close, where it
    /// would talk over the two moments that carry the video.
    /// </summary>
    private static IEnumerable<Placement> ResolvePlacements(
        JobManifest job,
        IReadOnlyList<PlanChapter> chapters,
        IReadOnlyList<string> supporting)
    {
        var assignments = job.Plan.InsertAssignments;
        if (assignments.Count > 0)
        {
            foreach (var assignment in assignments.OrderBy(entry => entry.ChapterIndex))
            {
                if (assignment.ChapterIndex < 0 || assignment.ChapterIndex >= chapters.Count)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(assignment.Media))
                {
                    continue;
                }

                // An assignment names a file the operator chose; a file that has since moved is
                // skipped rather than silently replaced with whatever else is in the folder.
                var media = supporting.FirstOrDefault(candidate =>
                    string.Equals(candidate, assignment.Media, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        Path.GetFileName(candidate),
                        Path.GetFileName(assignment.Media),
                        StringComparison.OrdinalIgnoreCase));

                if (media is null)
                {
                    continue;
                }

                yield return new Placement(
                    chapters[assignment.ChapterIndex],
                    media,
                    assignment.OffsetSeconds,
                    assignment.DurationSeconds);
            }

            yield break;
        }

        var bodyChapters = chapters.Where(chapter => chapter.Role == "beat").ToList();
        if (bodyChapters.Count == 0)
        {
            bodyChapters = chapters.Skip(1).Take(Math.Max(0, chapters.Count - 2)).ToList();
        }

        for (var index = 0; index < supporting.Count && index < bodyChapters.Count; index++)
        {
            yield return new Placement(bodyChapters[index], supporting[index], -1, 0);
        }
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

    /// <summary>
    /// Assigns a framing to each chapter so the video cuts between wide, medium and close on the
    /// one continuous plate. The hook opens wide to establish, the close ends wide to settle, and
    /// the body alternates so no two neighbouring chapters share a framing — a cut between two
    /// identical framings reads as a glitch rather than an edit.
    /// </summary>
    internal static IReadOnlyList<Shot> BuildShots(IReadOnlyList<PlanChapter> chapters, double programDuration)
    {
        var shots = new List<Shot>();
        if (chapters.Count == 0)
        {
            return shots;
        }

        // Scale is how far into the plate the frame sits; the bias lifts a tighter frame onto the face.
        var medium = (Label: "medium", Scale: 1.10, YBias: -0.28);
        var close = (Label: "close", Scale: 1.22, YBias: -0.45);
        var wide = (Label: "wide", Scale: 1.0, YBias: 0.0);

        var alternate = 0;
        for (var index = 0; index < chapters.Count; index++)
        {
            var chapter = chapters[index];

            // A framing needs time to register; anything shorter stays wide.
            if (chapter.DurationSeconds < 2.5)
            {
                shots.Add(ToShot(wide, chapter));
                continue;
            }

            var isOpening = index == 0;
            var isClosing = index == chapters.Count - 1;

            if (isOpening || isClosing)
            {
                shots.Add(ToShot(wide, chapter));
                continue;
            }

            var choice = alternate++ % 2 == 0 ? medium : close;
            shots.Add(ToShot(choice, chapter));
        }

        // Clamp the last shot to the program so a trailing chapter cannot run past the render.
        if (shots.Count > 0 && shots[^1].EndSeconds > programDuration)
        {
            shots[^1].DurationSeconds = Math.Max(0.1, programDuration - shots[^1].StartSeconds);
        }

        return shots;

        static Shot ToShot((string Label, double Scale, double YBias) framing, PlanChapter chapter) => new()
        {
            Label = $"{framing.Label} — {chapter.Title}",
            StartSeconds = chapter.StartSeconds,
            DurationSeconds = chapter.DurationSeconds,
            Scale = framing.Scale,
            YBias = framing.YBias,
        };
    }

    /// <summary>Chapter boundaries come from the real measured narration, not from estimates.</summary>
    internal static List<PlanChapter> BuildChapters(ScriptDocument script, NarrationResult narration)
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
