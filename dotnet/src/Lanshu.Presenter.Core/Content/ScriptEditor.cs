using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Content;

public sealed record ScriptEditResult(
    ScriptDocument Script,
    bool AudioInvalidated,
    double EstimatedSeconds);

/// <summary>
/// Reads and writes the drafted script for a job. Editing the wording after narration has been
/// synthesized invalidates that audio: the locked narration is the timeline for captions,
/// callouts and every cut, so it has to be spoken again rather than left out of sync.
/// </summary>
public sealed class ScriptEditor
{
    private readonly JobService _jobs = new();

    public ScriptDocument? Read(JobPaths paths)
    {
        var path = Path.Combine(paths.Docs, "script.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JobJson.Deserialize<ScriptDocument>(File.ReadAllText(path));
    }

    public ScriptEditResult Save(
        JobPaths paths,
        JobManifest job,
        IReadOnlyList<ScriptBeat> beats,
        string? title = null,
        bool approve = false)
    {
        var existing = Read(paths) ?? new ScriptDocument { Source = "edited" };

        var cleaned = beats
            .Select(beat => new ScriptBeat
            {
                Role = string.IsNullOrWhiteSpace(beat.Role) ? "beat" : beat.Role.Trim().ToLowerInvariant(),
                Title = (beat.Title ?? string.Empty).Trim(),
                // Anything pasted in from a document must arrive as speakable words only.
                Narration = TextUtil.ToSpokenText(beat.Narration ?? string.Empty),
                Keyword = (beat.Keyword ?? string.Empty).Trim(),
                VisualNote = (beat.VisualNote ?? string.Empty).Trim(),
            })
            .Where(beat => beat.Narration.Length > 0)
            .ToList();

        if (cleaned.Count == 0)
        {
            throw new ArgumentException("the script must contain at least one beat with narration");
        }

        var previousNarration = existing.Narration;

        var document = new ScriptDocument
        {
            Title = string.IsNullOrWhiteSpace(title) ? existing.Title : title.Trim(),
            Language = existing.Language,
            Source = existing.Source == "model" || existing.Source == "supplied"
                ? existing.Source + "+edited"
                : existing.Source,
            Provider = existing.Provider,
            Model = existing.Model,
            Beats = cleaned,
            Pronunciations = existing.Pronunciations,
            Notes = existing.Notes,
        };

        if (string.IsNullOrWhiteSpace(document.Title))
        {
            document.Title = TextUtil.Truncate(cleaned[0].Narration, 60);
        }

        document.Language = TextUtil.DetectLanguage(document.Narration);
        document.EnsureKeywords();

        FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "script.json"), JobJson.Serialize(document));
        FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "SCRIPT.md"), document.ToScriptMarkdown());
        FileSystemUtil.WriteAtomic(Path.Combine(paths.Docs, "BEAT_SHEET.md"), document.ToBeatSheetMarkdown());

        job.Artifacts.Script = paths.Relative(Path.Combine(paths.Docs, "SCRIPT.md"));
        job.Artifacts.BeatSheet = paths.Relative(Path.Combine(paths.Docs, "BEAT_SHEET.md"));
        job.Creative.Language = document.Language;

        var wordingChanged = !string.Equals(previousNarration, document.Narration, StringComparison.Ordinal);
        var audioInvalidated = false;

        if (wordingChanged && job.StateValue >= JobState.AudioLocked)
        {
            InvalidateAudio(paths, job);
            audioInvalidated = true;
        }

        if (approve)
        {
            job.Plan.ScriptApproved = true;
        }

        job.Record(
            "content_locked",
            audioInvalidated
                ? "script edited; locked narration invalidated and will be re-synthesized"
                : "script edited");

        _jobs.Save(paths, job);

        return new ScriptEditResult(document, audioInvalidated, document.EstimatedSeconds);
    }

    /// <summary>
    /// Drops back to content_locked and clears the artifacts that were derived from the old
    /// wording, so the next run re-speaks and re-times instead of reusing stale audio.
    /// </summary>
    private static void InvalidateAudio(JobPaths paths, JobManifest job)
    {
        job.StateValue = JobState.ContentLocked;
        job.Voice.Sections.Clear();
        job.Artifacts.FinalAudio = string.Empty;
        job.Artifacts.CaptionJson = string.Empty;
        job.Artifacts.CaptionSrt = string.Empty;
        job.Artifacts.CaptionAss = string.Empty;
        job.Artifacts.PresenterPlate = string.Empty;
        job.Artifacts.Rendered = string.Empty;

        foreach (var directory in new[] { paths.AudioRaw, paths.AudioReference, paths.AudioFinal })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                FileSystemUtil.TryDelete(file);
            }
        }

        // Captions and the presenter plate are timed against the old audio; both are rebuilt.
        foreach (var file in new[]
                 {
                     Path.Combine(paths.Captions, "captions.json"),
                     Path.Combine(paths.Captions, "captions.srt"),
                     Path.Combine(paths.Captions, "captions.ass"),
                     Path.Combine(paths.VideoSelected, "presenter.mp4"),
                     Path.Combine(paths.Renders, "rendered.mkv"),
                     Path.Combine(paths.Renders, "preview.mp4"),
                 })
        {
            FileSystemUtil.TryDelete(file);
        }
    }
}
