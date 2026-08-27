using System.Collections.Concurrent;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Media;

/// <summary>
/// How much the encode should favour quality over speed. The concrete numbers differ per
/// encoder, so callers ask for an intent rather than a CRF.
/// </summary>
public enum EncodeQuality
{
    /// <summary>Throwaway proxy: as fast as the machine can manage.</summary>
    Preview,

    /// <summary>Intermediate that will be re-encoded later; visually lossless enough.</summary>
    Intermediate,

    /// <summary>Delivery master.</summary>
    Master,

    /// <summary>Smaller share copy.</summary>
    Share,
}

/// <summary>
/// One H.264 encoder and the arguments that drive it. Quality flags are not portable between
/// encoders — x264 takes <c>-crf</c>, NVENC takes <c>-cq</c>, QSV takes <c>-global_quality</c>,
/// VideoToolbox has no constant-quality mode at all — so each profile builds its own.
/// </summary>
public sealed class VideoEncoderProfile
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public bool IsHardware { get; init; }

    /// <summary>Builds the encoder flags for a quality intent. Set only by the profiles below.</summary>
    private Func<EncodeQuality, IReadOnlyList<string>> ArgumentFactory { get; init; } =
        _ => Array.Empty<string>();

    public IReadOnlyList<string> Arguments(EncodeQuality quality) => ArgumentFactory(quality);

    public static readonly VideoEncoderProfile Software = new()
    {
        Name = "libx264",
        DisplayName = "libx264 (software)",
        IsHardware = false,
        ArgumentFactory = quality => quality switch
        {
            EncodeQuality.Preview => new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28" },
            EncodeQuality.Intermediate => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18" },
            EncodeQuality.Master => new[] { "-c:v", "libx264", "-preset", "slow", "-crf", "16", "-profile:v", "high" },
            _ => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "24", "-profile:v", "high" },
        },
    };

    /// <summary>NVIDIA. p1 is fastest, p7 slowest; -cq is its constant-quality control.</summary>
    public static readonly VideoEncoderProfile Nvenc = new()
    {
        Name = "h264_nvenc",
        DisplayName = "NVENC (NVIDIA)",
        IsHardware = true,
        ArgumentFactory = quality => quality switch
        {
            EncodeQuality.Preview => new[] { "-c:v", "h264_nvenc", "-preset", "p1", "-rc", "vbr", "-cq", "34" },
            EncodeQuality.Intermediate => new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "22" },
            EncodeQuality.Master => new[] { "-c:v", "h264_nvenc", "-preset", "p6", "-rc", "vbr", "-cq", "19", "-profile:v", "high" },
            _ => new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "27", "-profile:v", "high" },
        },
    };

    /// <summary>Intel Quick Sync.</summary>
    public static readonly VideoEncoderProfile Qsv = new()
    {
        Name = "h264_qsv",
        DisplayName = "Quick Sync (Intel)",
        IsHardware = true,
        ArgumentFactory = quality => quality switch
        {
            EncodeQuality.Preview => new[] { "-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "34" },
            EncodeQuality.Intermediate => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "22" },
            EncodeQuality.Master => new[] { "-c:v", "h264_qsv", "-preset", "slow", "-global_quality", "19", "-profile:v", "high" },
            _ => new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "27", "-profile:v", "high" },
        },
    };

    /// <summary>AMD. AMF exposes quality presets rather than a CRF.</summary>
    public static readonly VideoEncoderProfile Amf = new()
    {
        Name = "h264_amf",
        DisplayName = "AMF (AMD)",
        IsHardware = true,
        ArgumentFactory = quality => quality switch
        {
            EncodeQuality.Preview => new[] { "-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "32", "-qp_p", "34" },
            EncodeQuality.Intermediate => new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "22", "-qp_p", "24" },
            EncodeQuality.Master => new[] { "-c:v", "h264_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", "19", "-qp_p", "21", "-profile:v", "high" },
            _ => new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "26", "-qp_p", "28", "-profile:v", "high" },
        },
    };

    /// <summary>Apple. -q:v runs 1..100 here, the opposite direction from a CRF.</summary>
    public static readonly VideoEncoderProfile VideoToolbox = new()
    {
        Name = "h264_videotoolbox",
        DisplayName = "VideoToolbox (Apple)",
        IsHardware = true,
        ArgumentFactory = quality => quality switch
        {
            EncodeQuality.Preview => new[] { "-c:v", "h264_videotoolbox", "-q:v", "35" },
            EncodeQuality.Intermediate => new[] { "-c:v", "h264_videotoolbox", "-q:v", "60" },
            EncodeQuality.Master => new[] { "-c:v", "h264_videotoolbox", "-q:v", "72", "-profile:v", "high" },
            _ => new[] { "-c:v", "h264_videotoolbox", "-q:v", "50", "-profile:v", "high" },
        },
    };

    public static readonly IReadOnlyList<VideoEncoderProfile> All =
        new[] { Nvenc, Qsv, VideoToolbox, Amf, Software };

    public static VideoEncoderProfile? ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "software" or "libx264" or "x264" => Software,
            "nvenc" or "h264_nvenc" or "nvidia" => Nvenc,
            "qsv" or "h264_qsv" or "intel" or "quicksync" => Qsv,
            "amf" or "h264_amf" or "amd" => Amf,
            "videotoolbox" or "h264_videotoolbox" or "apple" => VideoToolbox,
            _ => null,
        };
    }
}

/// <summary>
/// Picks the encoder to use. Being listed by <c>ffmpeg -encoders</c> is not proof it works —
/// a build can advertise NVENC on a machine with no NVIDIA device — so each candidate is
/// proven once by encoding a few synthetic frames before it is trusted.
/// </summary>
public sealed class VideoEncoderSelector
{
    private static readonly ConcurrentDictionary<string, bool> ProbeCache = new(StringComparer.Ordinal);

    private readonly MediaToolset _toolset;
    private readonly Action<string>? _log;

    public VideoEncoderSelector(MediaToolset toolset, Action<string>? log = null)
    {
        _toolset = toolset;
        _log = log;
    }

    /// <summary>
    /// Resolves the preference to a working encoder. "software" is always honoured without a
    /// probe; "auto" tries hardware first and silently falls back.
    /// </summary>
    public async Task<VideoEncoderProfile> ResolveAsync(
        string? preference,
        CancellationToken cancellationToken = default)
    {
        var requested = (preference ?? "auto").Trim().ToLowerInvariant();

        if (requested is "software" or "libx264" or "x264")
        {
            return VideoEncoderProfile.Software;
        }

        if (requested is not ("auto" or ""))
        {
            var named = VideoEncoderProfile.ByName(requested);
            if (named is null)
            {
                _log?.Invoke($"Unknown encoder '{requested}'; using software encoding.");
                return VideoEncoderProfile.Software;
            }

            if (await IsUsableAsync(named, cancellationToken).ConfigureAwait(false))
            {
                return named;
            }

            _log?.Invoke($"{named.DisplayName} is not usable on this machine; using software encoding.");
            return VideoEncoderProfile.Software;
        }

        foreach (var candidate in VideoEncoderProfile.All.Where(profile => profile.IsHardware))
        {
            if (await IsUsableAsync(candidate, cancellationToken).ConfigureAwait(false))
            {
                _log?.Invoke($"Using {candidate.DisplayName} for video encoding.");
                return candidate;
            }
        }

        return VideoEncoderProfile.Software;
    }

    public async Task<bool> IsUsableAsync(VideoEncoderProfile profile, CancellationToken cancellationToken = default)
    {
        if (!profile.IsHardware)
        {
            return _toolset.Encoders.Contains(profile.Name);
        }

        if (!_toolset.Encoders.Contains(profile.Name))
        {
            return false;
        }

        var key = _toolset.FfmpegPath + "|" + profile.Name;
        if (ProbeCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=0.4",
        };
        arguments.AddRange(profile.Arguments(EncodeQuality.Preview));
        arguments.AddRange(new[] { "-pix_fmt", "yuv420p", "-f", "null", "-" });

        bool usable;
        try
        {
            var result = await ProcessRunner
                .RunAsync(_toolset.FfmpegPath, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            usable = result.Success;
            if (!usable)
            {
                _log?.Invoke($"{profile.DisplayName} probe failed: {ProcessResult.Tail(result.StandardError, 3)}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            usable = false;
        }

        ProbeCache[key] = usable;
        return usable;
    }

    /// <summary>Lists the hardware encoders that actually work here, for the environment report.</summary>
    public async Task<IReadOnlyList<string>> AvailableHardwareAsync(CancellationToken cancellationToken = default)
    {
        var available = new List<string>();
        foreach (var profile in VideoEncoderProfile.All.Where(candidate => candidate.IsHardware))
        {
            if (await IsUsableAsync(profile, cancellationToken).ConfigureAwait(false))
            {
                available.Add(profile.DisplayName);
            }
        }

        return available;
    }

    internal static void ClearProbeCacheForTests() => ProbeCache.Clear();
}
