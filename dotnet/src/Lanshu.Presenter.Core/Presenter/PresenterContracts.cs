using Lanshu.Presenter.Core.Models;

namespace Lanshu.Presenter.Core.Presenter;

public sealed record PresenterRequest
{
    public required string ImagePath { get; init; }

    public required string AudioPath { get; init; }

    public required string OutputPath { get; init; }

    public required double DurationSeconds { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Fps { get; init; }

    public string Prompt { get; init; } = string.Empty;

    public string NegativePrompt { get; init; } = string.Empty;

    /// <summary>A pilot is the smallest useful sample generated before any full paid run.</summary>
    public bool IsPilot { get; init; }
}

public sealed record PresenterPlate(
    string Path,
    double DurationSeconds,
    string Provider,
    string Model,
    bool HasSynchronizedMouth,
    IReadOnlyList<string> TaskIds)
{
    public static PresenterPlate Local(string path, double duration, string provider, string model) =>
        new(path, duration, provider, model, HasSynchronizedMouth: false, Array.Empty<string>());
}

public interface IPresenterGenerator
{
    string Provider { get; }

    string Model { get; }

    bool IsRemote { get; }

    /// <summary>True when the generator drives the mouth from the supplied narration.</summary>
    bool ProducesLipSync { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<PresenterPlate> GenerateAsync(PresenterRequest request, CancellationToken cancellationToken = default);
}

public sealed class PresenterGenerationException : Exception
{
    public PresenterGenerationException(string message) : base(message)
    {
    }

    public PresenterGenerationException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Builds the presenter prompt in the order generation.md prescribes: bind the image, list only
/// visible anchors, request realistic micro-motion, then exclude every kind of identity drift.
/// </summary>
public static class PresenterPromptBuilder
{
    public static string Build(JobManifest job, bool actionfulOpening)
    {
        var creative = job.Creative;
        var lines = new List<string>
        {
            "Use the supplied reference image as the sole presenter and the sole scene reference. Keep the same person, the same wardrobe, the same accessories, the same background, and the same camera.",
            "Preserve every visible anchor from the reference image: framing, crop, camera height, hair, glasses, clothing, accessories, background geometry, light direction, exposure, and white balance.",
            "Render realistic skin texture, hair detail, eye moisture, fabric behaviour, natural breathing, sparse bilateral blinking, and restrained head motion.",
            "Speak the supplied narration audio exactly. Mouth shapes must follow the audio, including names, numbers, and phrase endings.",
        };

        lines.Add(actionfulOpening
            ? "Allow one small, physically plausible hand gesture tied to the opening phrase. Keep hands below the collarbone, away from the face and the lens, and finish the movement before the tail."
            : "Keep hands low or outside the frame. No gesturing during the main body.");

        lines.Add("Reserve a settled, closed-mouth tail at the end with no residual motion.");

        if (!string.IsNullOrWhiteSpace(creative.Style))
        {
            lines.Add($"Overall treatment: {creative.Style}.");
        }

        lines.Add(
            "Exclude: identity drift, face or jaw reshaping, changed glasses, changed hair, changed wardrobe, changed skin tone, changed background, camera moves, extra fingers, malformed hands, extra people, on-screen text, logos, watermarks, and subtitles.");

        return string.Join("\n", lines);
    }

    public static string NegativePrompt() =>
        "identity drift, different person, face morphing, distorted jaw, extra fingers, malformed hands, extra limbs, extra people, changed glasses, changed hairstyle, changed clothing, changed background, camera shake, zoom, text, captions, watermark, logo, blur, low resolution";
}
