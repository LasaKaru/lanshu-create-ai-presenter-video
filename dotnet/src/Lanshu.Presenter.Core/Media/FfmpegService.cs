using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Media;

public sealed record LoudnessMeasurement(
    double InputI,
    double InputTp,
    double InputLra,
    double InputThresh,
    double TargetOffset)
{
    public string ToLoudnormFilter(double targetI, double truePeak, double lra) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "loudnorm=I={0}:TP={1}:LRA={2}:measured_I={3}:measured_TP={4}:measured_LRA={5}:measured_thresh={6}:offset={7}:linear=true:print_format=summary",
            targetI,
            truePeak,
            lra,
            InputI,
            InputTp,
            InputLra,
            InputThresh,
            TargetOffset);
}

/// <summary>
/// Every ffmpeg and ffprobe invocation in the app goes through here so arguments,
/// logging and failure reporting stay consistent.
/// </summary>
public sealed class FfmpegService
{
    private readonly MediaToolset _toolset;
    private readonly Action<string>? _log;

    public FfmpegService(MediaToolset toolset, Action<string>? log = null)
    {
        _toolset = toolset;
        _log = log;
    }

    public MediaToolset Toolset => _toolset;

    /// <summary>
    /// The encoder chosen for this run. Defaults to software so any caller that never resolves
    /// one still produces a correct encode.
    /// </summary>
    public VideoEncoderProfile Encoder { get; set; } = VideoEncoderProfile.Software;

    /// <summary>
    /// Runs an encode whose video codec flags come from <see cref="Encoder"/>. A hardware
    /// encoder can pass its start-up probe and still fail mid-run — a busy GPU, a driver reset,
    /// a resolution it will not accept — so a hardware failure is retried once in software
    /// rather than failing the job.
    /// </summary>
    public async Task RunEncodeAsync(
        string what,
        EncodeQuality quality,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> buildArguments,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null)
    {
        var result = await RunAsync(buildArguments(Encoder.Arguments(quality)), cancellationToken, workingDirectory)
            .ConfigureAwait(false);

        if (result.Success)
        {
            return;
        }

        if (!Encoder.IsHardware)
        {
            result.EnsureSuccess(what);
            return;
        }

        _log?.Invoke(
            $"{Encoder.DisplayName} failed during {what}; retrying with software encoding. " +
            ProcessResult.Tail(result.StandardError, 3));

        Encoder = VideoEncoderProfile.Software;
        var retry = await RunAsync(
                buildArguments(VideoEncoderProfile.Software.Arguments(quality)),
                cancellationToken,
                workingDirectory)
            .ConfigureAwait(false);
        retry.EnsureSuccess(what);
    }

    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var arguments = new[]
        {
            "-v", "error",
            "-show_streams",
            "-show_format",
            "-of", "json",
            path,
        };

        var result = await ProcessRunner
            .RunAsync(_toolset.FfprobePath, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        result.EnsureSuccess($"ffprobe on {Path.GetFileName(path)}");

        var probe = JsonSerializer.Deserialize<MediaProbe>(result.StandardOutput, ProbeJsonOptions)
            ?? throw new ExternalToolException($"ffprobe returned no usable data for {Path.GetFileName(path)}");
        probe.Raw = JsonNode.Parse(result.StandardOutput);
        return probe;
    }

    public async Task<double> DurationAsync(string path, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        return probe.DurationSeconds;
    }

    public async Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null)
    {
        var argumentList = arguments.ToList();
        _log?.Invoke("ffmpeg " + string.Join(' ', argumentList.Select(Quote)));
        return await ProcessRunner
            .RunAsync(
                _toolset.FfmpegPath,
                argumentList,
                workingDirectory: workingDirectory,
                onOutputLine: LogFfmpegLine,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RunCheckedAsync(
        string what,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null)
    {
        var result = await RunAsync(arguments, cancellationToken, workingDirectory).ConfigureAwait(false);
        result.EnsureSuccess(what);
    }

    /// <summary>
    /// Escapes a value embedded inside a filtergraph option, where ':' separates options and
    /// backslashes and quotes are meaningful. Windows paths need this for fontsdir.
    /// </summary>
    public static string EscapeFilterValue(string value) =>
        (value ?? string.Empty)
        .Replace("\\", "/")
        .Replace(":", "\\:")
        .Replace("'", "\\'")
        .Replace("[", "\\[")
        .Replace("]", "\\]")
        .Replace(",", "\\,");

    /// <summary>Pass 1 of the two-pass EBU R128 normalization used at delivery.</summary>
    public async Task<LoudnessMeasurement> MeasureLoudnessAsync(
        string inputPath,
        double targetI = -16,
        double truePeak = -1.5,
        double lra = 9,
        CancellationToken cancellationToken = default)
    {
        var filter = string.Format(
            CultureInfo.InvariantCulture,
            "loudnorm=I={0}:TP={1}:LRA={2}:print_format=json",
            targetI,
            truePeak,
            lra);

        var arguments = new[]
        {
            "-hide_banner", "-nostdin", "-nostats",
            "-i", inputPath,
            "-map", "0:a:0", "-vn",
            "-af", filter,
            "-f", "null", "-",
        };

        var result = await ProcessRunner
            .RunAsync(_toolset.FfmpegPath, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        result.EnsureSuccess("loudness measurement");

        var json = ExtractTrailingJson(result.StandardError)
            ?? throw new ExternalToolException("could not parse the loudness measurement from ffmpeg output");

        var node = JsonNode.Parse(json)
            ?? throw new ExternalToolException("loudness measurement JSON was empty");

        return new LoudnessMeasurement(
            ReadDouble(node, "input_i"),
            ReadDouble(node, "input_tp"),
            ReadDouble(node, "input_lra"),
            ReadDouble(node, "input_thresh"),
            ReadDouble(node, "target_offset"));
    }

    /// <summary>Decodes every packet and fails on the first error, matching the shell finalizer's -xerror gate.</summary>
    public async Task<bool> FullDecodeAsync(string path, bool requireAudio, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-v", "error", "-xerror",
            "-i", path,
            "-map", "0:v:0",
        };

        if (requireAudio)
        {
            arguments.AddRange(new[] { "-map", "0:a:0" });
        }

        arguments.AddRange(new[] { "-f", "null", "-" });

        var result = await ProcessRunner
            .RunAsync(_toolset.FfmpegPath, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Success;
    }

    public async Task<int> CountBlackFrameEventsAsync(string path, CancellationToken cancellationToken = default)
    {
        var arguments = new[]
        {
            "-hide_banner", "-nostdin",
            "-i", path,
            "-vf", "blackdetect=d=0.10:pix_th=0.02",
            "-an", "-f", "null", "-",
        };

        var result = await ProcessRunner
            .RunAsync(_toolset.FfmpegPath, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return Regex.Matches(result.Combined, "black_start:").Count;
    }

    /// <summary>Extracts one frame, used for cover frames and contact-sheet tiles.</summary>
    public async Task ExtractFrameAsync(
        string source,
        double timestampSeconds,
        string destination,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-ss", timestampSeconds.ToString("0.######", CultureInfo.InvariantCulture),
            "-i", source,
            "-frames:v", "1",
        };

        if (!string.IsNullOrWhiteSpace(filter))
        {
            arguments.AddRange(new[] { "-vf", filter });
        }

        arguments.AddRange(new[] { "-update", "1", "-y", destination });
        await RunCheckedAsync("frame extraction", arguments, cancellationToken).ConfigureAwait(false);
    }

    private void LogFfmpegLine(string line)
    {
        if (_log is null)
        {
            return;
        }

        // ffmpeg's progress spam is noise in a job log; keep warnings, errors and the summary.
        if (line.StartsWith("frame=", StringComparison.Ordinal)
            || line.StartsWith("size=", StringComparison.Ordinal))
        {
            return;
        }

        _log(line);
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static double ReadDouble(JsonNode node, string key)
    {
        var value = node[key]?.GetValue<object>()?.ToString();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    /// <summary>loudnorm prints its JSON block at the end of stderr, after the usual log lines.</summary>
    internal static string? ExtractTrailingJson(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var start = text.LastIndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return text.Substring(start, end - start + 1);
    }

    private static readonly JsonSerializerOptions ProbeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };
}
