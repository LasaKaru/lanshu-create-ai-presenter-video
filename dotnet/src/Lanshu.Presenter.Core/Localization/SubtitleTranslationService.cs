using Lanshu.Presenter.Core.Captions;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Localization;

public sealed record TranslatedSubtitle(string Language, string Path, int Lines);

public sealed record SubtitleTranslationResult(
    IReadOnlyList<TranslatedSubtitle> Files,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Writes translated .srt sidecars beside the delivered video.
///
/// Only the words change. The timings come from the one recording that was actually made, so a
/// translated sidecar is the same cue list with different text — which is exactly why it is cheap
/// and exactly why it is not a dub. The video still speaks the original language, and the job
/// record says so rather than implying the audience will hear their own.
/// </summary>
public sealed class SubtitleTranslationService
{
    private readonly ITranslator _translator;
    private readonly Action<string>? _log;

    public SubtitleTranslationService(ITranslator translator, Action<string>? log = null)
    {
        _translator = translator;
        _log = log;
    }

    public async Task<SubtitleTranslationResult> RunAsync(
        JobPaths paths,
        CaptionPlan plan,
        string sourceLanguage,
        IReadOnlyList<string> targetLanguages,
        string stem,
        CancellationToken cancellationToken = default)
    {
        var files = new List<TranslatedSubtitle>();
        var warnings = new List<string>();

        var cues = plan.Phrases
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase.Text))
            .ToList();

        if (cues.Count == 0)
        {
            warnings.Add("there are no caption phrases to translate");
            return new SubtitleTranslationResult(files, warnings);
        }

        var source = cues.Select(phrase => phrase.Text).ToList();

        foreach (var language in targetLanguages.Select(Normalize).Where(language => language.Length > 0).Distinct())
        {
            if (string.Equals(language, sourceLanguage, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _log?.Invoke($"Translating {cues.Count} caption lines into {language} via {_translator.Provider}");
                var translated = await _translator
                    .TranslateAsync(source, language, sourceLanguage, cancellationToken)
                    .ConfigureAwait(false);

                // Build a plan that keeps every original timing and swaps only the words.
                var localized = new CaptionPlan { WordTimingsAreMeasured = plan.WordTimingsAreMeasured };
                for (var index = 0; index < cues.Count; index++)
                {
                    localized.Phrases.Add(new CaptionPhrase
                    {
                        Index = index,
                        Text = translated[index],
                        StartSeconds = cues[index].StartSeconds,
                        EndSeconds = cues[index].EndSeconds,
                    });
                }

                var path = Path.Combine(paths.Outputs, $"{stem}.{Slug(language)}.srt");
                FileSystemUtil.WriteAtomic(path, SrtWriter.Build(localized));
                files.Add(new TranslatedSubtitle(language, path, localized.Phrases.Count));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One language failing must not cost the others, or the whole delivery.
                warnings.Add($"{language} subtitles were not written: {exception.Message}");
            }
        }

        return new SubtitleTranslationResult(files, warnings);
    }

    private static string Normalize(string language) => (language ?? string.Empty).Trim();

    /// <summary>
    /// A language name has to survive being part of a filename. The blank case is checked before
    /// slugifying, because Slugify substitutes its own generic fallback for an empty string and
    /// a sidecar called "job.presenter-video.srt" tells the reader nothing about its language.
    /// </summary>
    internal static string Slug(string language) =>
        string.IsNullOrWhiteSpace(language) ? "translated" : FileSystemUtil.Slugify(language);
}
