using System.Globalization;
using System.Text;
using Lanshu.Presenter.Core.Content;
using Lanshu.Presenter.Core.Jobs;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Voice;

public sealed record NarrationSegment(
    int Index,
    int BeatIndex,
    string Text,
    string File,
    double StartSeconds,
    double DurationSeconds)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}

public sealed record NarrationResult(
    string AudioPath,
    double DurationSeconds,
    IReadOnlyList<NarrationSegment> Segments,
    string Provider,
    string Model,
    string VoiceId);

/// <summary>
/// Produces the complete narration that acts as the master clock for the whole video.
/// One voice identity, one configuration, consistent per-segment loudness, real measured
/// durations — everything downstream positions against this file.
/// </summary>
public sealed class NarrationService
{
    private const double BeatGapSeconds = 0.42;
    private const double SegmentGapSeconds = 0.18;
    private const double TailHoldSeconds = 0.55;

    private readonly FfmpegService _ffmpeg;
    private readonly Action<string>? _log;

    public NarrationService(FfmpegService ffmpeg, Action<string>? log = null)
    {
        _ffmpeg = ffmpeg;
        _log = log;
    }

    public async Task<NarrationResult> BuildAsync(
        JobPaths paths,
        JobManifest job,
        ScriptDocument script,
        ISpeechSynthesizer synthesizer,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.AudioRaw);
        Directory.CreateDirectory(paths.AudioReference);
        Directory.CreateDirectory(paths.AudioFinal);

        var voiceId = string.IsNullOrWhiteSpace(job.Voice.VoiceId) ? string.Empty : job.Voice.VoiceId;
        var rate = job.Voice.Rate <= 0 ? 1.0 : job.Voice.Rate;
        var language = script.Language;

        var units = new List<(int BeatIndex, string Text)>();
        for (var beatIndex = 0; beatIndex < script.Beats.Count; beatIndex++)
        {
            foreach (var segment in TextUtil.BuildSegments(script.Beats[beatIndex].Narration))
            {
                units.Add((beatIndex, segment));
            }
        }

        if (units.Count == 0)
        {
            throw new SpeechSynthesisException("the script contains no narration to speak");
        }

        // Segments already spoken with the same words and the same voice configuration are kept.
        // Re-synthesizing them would cost real money on a paid engine and change nothing.
        var previous = job.Voice.Sections.ToDictionary(section => section.Index, section => section);
        var configurationChanged = !string.Equals(job.Voice.Provider, synthesizer.Provider, StringComparison.Ordinal);

        var segments = new List<NarrationSegment>();
        var reused = 0;

        for (var index = 0; index < units.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (beatIndex, text) = units[index];
            var stem = $"seg-{index + 1:00}";
            var normalizedPath = Path.Combine(paths.AudioReference, stem + ".wav");

            previous.TryGetValue(index, out var prior);
            var spokenText = string.IsNullOrWhiteSpace(prior?.SpokenOverride) ? text : prior!.SpokenOverride;

            if (!configurationChanged
                && prior is not null
                && !prior.RetakeRequested
                && string.Equals(prior.Text, text, StringComparison.Ordinal)
                && File.Exists(normalizedPath)
                && FileSystemUtil.SafeLength(normalizedPath) > 0)
            {
                var existingDuration = await _ffmpeg
                    .DurationAsync(normalizedPath, cancellationToken)
                    .ConfigureAwait(false);

                if (existingDuration > 0)
                {
                    segments.Add(new NarrationSegment(index, beatIndex, text, normalizedPath, 0, existingDuration));
                    reused++;
                    continue;
                }
            }

            var rawPath = Path.Combine(paths.AudioRaw, stem + SuggestExtension(synthesizer));
            _log?.Invoke(
                $"Synthesizing {stem} ({spokenText.Length} chars) with {synthesizer.Provider}"
                + (ReferenceEquals(spokenText, text) || spokenText == text ? string.Empty : " using a pronunciation override"));

            FileSystemUtil.TryDelete(rawPath);
            await synthesizer.SynthesizeAsync(
                new SpeechRequest(spokenText, rawPath)
                {
                    VoiceId = voiceId,
                    Rate = rate,
                    Language = language,
                },
                cancellationToken).ConfigureAwait(false);

            await NormalizeSegmentAsync(rawPath, normalizedPath, job.Voice.SegmentLufs, cancellationToken)
                .ConfigureAwait(false);

            var duration = await _ffmpeg.DurationAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
            if (duration <= 0)
            {
                throw new SpeechSynthesisException($"segment {stem} produced no audible audio");
            }

            segments.Add(new NarrationSegment(index, beatIndex, text, normalizedPath, 0, duration));
        }

        // Any segment file left over from a longer previous script would confuse a later reuse pass.
        foreach (var stale in Directory
                     .EnumerateFiles(paths.AudioReference, "seg-*.wav")
                     .Where(file => !segments.Any(segment =>
                         string.Equals(segment.File, file, StringComparison.OrdinalIgnoreCase))))
        {
            FileSystemUtil.TryDelete(stale);
        }

        if (reused > 0)
        {
            _log?.Invoke($"Reused {reused} of {units.Count} already-spoken segments");
        }

        // Lay out the real durations with breathing gaps; this is the timeline everything else uses.
        var positioned = new List<NarrationSegment>(segments.Count);
        var cursor = 0.0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            positioned.Add(segment with { StartSeconds = cursor });
            cursor += segment.DurationSeconds;
            if (index < segments.Count - 1)
            {
                cursor += segments[index + 1].BeatIndex != segment.BeatIndex ? BeatGapSeconds : SegmentGapSeconds;
            }
        }

        var totalSeconds = cursor + TailHoldSeconds;
        var finalPath = Path.Combine(paths.AudioFinal, "narration.wav");
        FileSystemUtil.TryDelete(finalPath);
        await AssembleAsync(positioned, totalSeconds, finalPath, cancellationToken).ConfigureAwait(false);

        var measured = await _ffmpeg.DurationAsync(finalPath, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"Narration locked: {measured:0.000}s across {positioned.Count} segments");

        job.Voice.Provider = synthesizer.Provider;
        job.Voice.Sections = positioned
            .Select(segment =>
            {
                previous.TryGetValue(segment.Index, out var prior);
                return new VoiceSection
                {
                    Index = segment.Index,
                    Text = segment.Text,
                    // A pronunciation override survives a re-run; a re-take request is consumed by it.
                    SpokenOverride = prior?.SpokenOverride ?? string.Empty,
                    RetakeRequested = false,
                    File = paths.Relative(segment.File),
                    DurationSeconds = Math.Round(segment.DurationSeconds, 3),
                    StartSeconds = Math.Round(segment.StartSeconds, 3),
                };
            })
            .ToList();

        job.Capabilities.VoiceGeneration.Provider = synthesizer.Provider;
        job.Capabilities.VoiceGeneration.Model = synthesizer.Model;
        job.Capabilities.VoiceGeneration.Notes =
            $"{positioned.Count} segments, one voice configuration, per-segment target {job.Voice.SegmentLufs} LUFS";
        job.Capabilities.VoiceGeneration.Parameters["voice_id"] = voiceId;
        job.Capabilities.VoiceGeneration.Parameters["rate"] = rate;
        job.Artifacts.FinalAudio = paths.Relative(finalPath);

        return new NarrationResult(
            finalPath,
            measured,
            positioned,
            synthesizer.Provider,
            synthesizer.Model,
            voiceId);
    }

    /// <summary>
    /// Normalizes one segment to a common loudness and a single mono 48k layout so
    /// concatenation never introduces level jumps or resample artifacts.
    /// </summary>
    private async Task NormalizeSegmentAsync(
        string source,
        string destination,
        double targetLufs,
        CancellationToken cancellationToken)
    {
        var filter = string.Format(
            CultureInfo.InvariantCulture,
            "loudnorm=I={0}:TP=-2.0:LRA=9,aresample=48000,highpass=f=70,alimiter=limit=0.94",
            targetLufs);

        var arguments = new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
            "-i", source,
            "-map", "0:a:0", "-vn",
            "-af", filter,
            "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "1",
            destination,
        };

        await _ffmpeg.RunCheckedAsync($"segment normalization for {Path.GetFileName(source)}", arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Places each normalized segment at its authored start on one silent bed. Using adelay
    /// against a fixed-length bed keeps the gaps exact instead of accumulating concat drift.
    /// </summary>
    private async Task AssembleAsync(
        IReadOnlyList<NarrationSegment> segments,
        double totalSeconds,
        string destination,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
            "-f", "lavfi",
            "-t", totalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", "anullsrc=channel_layout=mono:sample_rate=48000",
        };

        foreach (var segment in segments)
        {
            arguments.AddRange(new[] { "-i", segment.File });
        }

        var filter = new StringBuilder();
        var labels = new List<string> { "[0:a]" };
        for (var index = 0; index < segments.Count; index++)
        {
            var delayMs = (int)Math.Round(segments[index].StartSeconds * 1000);
            filter.Append(CultureInfo.InvariantCulture, $"[{index + 1}:a]adelay={delayMs}:all=1[d{index}];");
            labels.Add($"[d{index}]");
        }

        filter.Append(string.Join(string.Empty, labels));
        filter.Append(CultureInfo.InvariantCulture, $"amix=inputs={labels.Count}:normalize=0:duration=first[out]");

        arguments.AddRange(new[]
        {
            "-filter_complex", filter.ToString(),
            "-map", "[out]",
            "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "1",
            destination,
        });

        await _ffmpeg.RunCheckedAsync("narration assembly", arguments, cancellationToken).ConfigureAwait(false);
    }

    private static string SuggestExtension(ISpeechSynthesizer synthesizer) => synthesizer.Provider switch
    {
        "elevenlabs" or "openai" => ".mp3",
        "macos-say" => ".aiff",
        _ => ".wav",
    };
}
