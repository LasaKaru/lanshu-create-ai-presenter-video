using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Voice;

public sealed record RetakeResult(int Index, string Text, string SpokenText, bool OverrideChanged);

/// <summary>
/// Marks one narration segment for re-synthesis, optionally with a different spoken wording.
///
/// The captions and keyword anchors keep the script's wording; only what reaches the speech
/// engine changes. That is what makes this useful for a mispronounced name — you respell it for
/// the engine without corrupting what the viewer reads.
///
/// Re-taking a segment changes its duration, which shifts every later segment, so the derived
/// audio, captions and render are invalidated. The other segments' audio is kept, which is the
/// point: on a paid engine only the one segment is paid for again.
/// </summary>
public sealed class SegmentRetakeService
{
    private readonly JobService _jobs = new();

    public RetakeResult Request(
        JobPaths paths,
        JobManifest job,
        int index,
        string? spokenOverride = null)
    {
        var section = job.Voice.Sections.FirstOrDefault(candidate => candidate.Index == index)
            ?? throw new ArgumentOutOfRangeException(
                nameof(index),
                $"this job has no narration segment {index}; it has {job.Voice.Sections.Count}");

        var previousOverride = section.SpokenOverride;
        if (spokenOverride is not null)
        {
            var cleaned = TextUtil.ToSpokenText(spokenOverride).Trim();

            // An override equal to the script wording is not an override at all.
            section.SpokenOverride =
                string.Equals(cleaned, section.Text, StringComparison.Ordinal) ? string.Empty : cleaned;
        }

        section.RetakeRequested = true;

        // The segment's own audio goes now so a crash between here and the next run cannot leave
        // a stale take that the reuse pass would happily keep.
        var segmentFile = paths.Resolve(section.File);
        if (!string.IsNullOrWhiteSpace(section.File) && File.Exists(segmentFile))
        {
            FileSystemUtil.TryDelete(segmentFile);
        }

        InvalidateDerivedAudio(paths, job);

        job.Record(
            "audio_locked",
            string.IsNullOrWhiteSpace(section.SpokenOverride)
                ? $"segment {index} marked for re-take"
                : $"segment {index} marked for re-take with a pronunciation override");

        _jobs.Save(paths, job);

        return new RetakeResult(
            index,
            section.Text,
            section.EffectiveSpokenText,
            !string.Equals(previousOverride, section.SpokenOverride, StringComparison.Ordinal));
    }

    /// <summary>
    /// Drops back to content_locked and clears what was timed against the old narration. The
    /// per-segment audio of every other segment is deliberately left in place.
    /// </summary>
    private static void InvalidateDerivedAudio(JobPaths paths, JobManifest job)
    {
        if (job.StateValue > JobState.ContentLocked)
        {
            job.StateValue = JobState.ContentLocked;
        }

        job.Artifacts.FinalAudio = string.Empty;
        job.Artifacts.CaptionJson = string.Empty;
        job.Artifacts.CaptionSrt = string.Empty;
        job.Artifacts.CaptionAss = string.Empty;
        job.Artifacts.PresenterPlate = string.Empty;
        job.Artifacts.Rendered = string.Empty;

        foreach (var file in Directory
                     .EnumerateFiles(paths.AudioFinal)
                     .Concat(new[]
                     {
                         Path.Combine(paths.Captions, "captions.json"),
                         Path.Combine(paths.Captions, "captions.srt"),
                         Path.Combine(paths.Captions, "captions.ass"),
                         Path.Combine(paths.VideoSelected, "presenter.mp4"),
                         Path.Combine(paths.VideoSelected, "presenter-lipsync.mp4"),
                         Path.Combine(paths.Renders, "rendered.mkv"),
                         Path.Combine(paths.Renders, "preview.mp4"),
                     }))
        {
            FileSystemUtil.TryDelete(file);
        }
    }

    /// <summary>Clears a pronunciation override and re-takes the segment with the script wording.</summary>
    public RetakeResult ClearOverride(JobPaths paths, JobManifest job, int index) =>
        Request(paths, job, index, string.Empty);
}
