namespace Lanshu.Presenter.Core.Voice;

public sealed record VoiceDescriptor(string Id, string DisplayName, string Language, string Provider);

public sealed record SpeechRequest(string Text, string OutputPath)
{
    public string VoiceId { get; init; } = string.Empty;

    /// <summary>Speaking-rate multiplier where 1.0 is the engine default.</summary>
    public double Rate { get; init; } = 1.0;

    public string Language { get; init; } = string.Empty;
}

/// <summary>
/// One voice identity and one configuration per job. Implementations must write a decodable
/// audio file at <see cref="SpeechRequest.OutputPath"/> or throw.
/// </summary>
public interface ISpeechSynthesizer
{
    string Provider { get; }

    string Model { get; }

    bool IsRemote { get; }

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default);

    Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default);
}

public sealed class SpeechSynthesisException : Exception
{
    public SpeechSynthesisException(string message) : base(message)
    {
    }

    public SpeechSynthesisException(string message, Exception inner) : base(message, inner)
    {
    }
}
