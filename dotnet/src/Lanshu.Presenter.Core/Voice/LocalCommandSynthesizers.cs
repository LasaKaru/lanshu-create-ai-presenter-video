using System.Globalization;
using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Voice;

/// <summary>Offline synthesis through espeak-ng. Robotic but always available on Linux CI and desktops.</summary>
public sealed class EspeakSynthesizer : ISpeechSynthesizer
{
    public string Provider => "espeak-ng";

    public string Model => "espeak-ng";

    public bool IsRemote => false;

    private static string? ExecutablePath => ToolLocator.Find("espeak-ng") ?? ToolLocator.Find("espeak");

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ExecutablePath is not null);

    public async Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath;
        if (executable is null)
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var result = await ProcessRunner
            .RunAsync(executable, new[] { "--voices" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var voices = new List<VoiceDescriptor>();
        var lines = result.StandardOutput.Replace("\r\n", "\n").Split('\n').Skip(1);
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            voices.Add(new VoiceDescriptor(parts[3], parts[3], parts[1], Provider));
        }

        return voices;
    }

    public async Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath
            ?? throw new SpeechSynthesisException("espeak-ng is not installed");

        // espeak's default is about 175 words per minute; scale it by the requested rate.
        var wordsPerMinute = Math.Clamp((int)Math.Round(165 * request.Rate), 80, 400);

        await SynthesisScratch.WriteThroughAsync(request.OutputPath, ".wav", async scratchPath =>
        {
            var arguments = new List<string>
            {
                "-w", scratchPath,
                "-s", wordsPerMinute.ToString(CultureInfo.InvariantCulture),
            };

            if (!string.IsNullOrWhiteSpace(request.VoiceId))
            {
                arguments.AddRange(new[] { "-v", request.VoiceId });
            }
            else if (!string.IsNullOrWhiteSpace(request.Language))
            {
                arguments.AddRange(new[] { "-v", request.Language });
            }

            arguments.Add("--");
            arguments.Add(request.Text);

            var result = await ProcessRunner
                .RunAsync(executable, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                throw new SpeechSynthesisException("espeak-ng failed: " + ProcessResult.Tail(result.Combined, 8));
            }
        }).ConfigureAwait(false);
    }
}

/// <summary>macOS `say`. Produces AIFF, which ffmpeg reads directly.</summary>
public sealed class MacSaySynthesizer : ISpeechSynthesizer
{
    public string Provider => "macos-say";

    public string Model => "macos-say";

    public bool IsRemote => false;

    private static string? ExecutablePath =>
        OperatingSystem.IsMacOS() ? ToolLocator.Find("say") : null;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ExecutablePath is not null);

    public async Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath;
        if (executable is null)
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var result = await ProcessRunner
            .RunAsync(executable, new[] { "-v", "?" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var voices = new List<VoiceDescriptor>();
        foreach (var line in result.StandardOutput.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            voices.Add(new VoiceDescriptor(parts[0], parts[0], parts[1], Provider));
        }

        return voices;
    }

    public async Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath
            ?? throw new SpeechSynthesisException("the macOS `say` command is not available");

        var wordsPerMinute = Math.Clamp((int)Math.Round(175 * request.Rate), 90, 400);

        await SynthesisScratch.WriteThroughAsync(request.OutputPath, ".aiff", async scratchPath =>
        {
            var arguments = new List<string> { "-o", scratchPath, "--data-format=LEF32@22050" };
            if (!string.IsNullOrWhiteSpace(request.VoiceId))
            {
                arguments.AddRange(new[] { "-v", request.VoiceId });
            }

            arguments.AddRange(new[] { "-r", wordsPerMinute.ToString(CultureInfo.InvariantCulture), request.Text });

            var result = await ProcessRunner
                .RunAsync(executable, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                throw new SpeechSynthesisException("macOS say failed: " + ProcessResult.Tail(result.Combined, 8));
            }
        }).ConfigureAwait(false);
    }
}

/// <summary>Offline neural voices through a local Piper install.</summary>
public sealed class PiperSynthesizer : ISpeechSynthesizer
{
    private readonly VoiceSettings _settings;

    public PiperSynthesizer(VoiceSettings settings)
    {
        _settings = settings;
    }

    public string Provider => "piper";

    public string Model => Path.GetFileNameWithoutExtension(_settings.PiperModelPath);

    public bool IsRemote => false;

    private static string? ExecutablePath => ToolLocator.Find("piper");

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ExecutablePath is not null
                        && !string.IsNullOrWhiteSpace(_settings.PiperModelPath)
                        && File.Exists(FileSystemUtil.ExpandPath(_settings.PiperModelPath)));

    public Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VoiceDescriptor> voices = string.IsNullOrWhiteSpace(_settings.PiperModelPath)
            ? Array.Empty<VoiceDescriptor>()
            : new[] { new VoiceDescriptor(_settings.PiperModelPath, Model, string.Empty, Provider) };
        return Task.FromResult(voices);
    }

    public async Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var executable = ExecutablePath
            ?? throw new SpeechSynthesisException("piper is not installed or not on PATH");
        var model = FileSystemUtil.ExpandPath(_settings.PiperModelPath);

        var textPath = Path.Combine(Path.GetTempPath(), $"lanshu-piper-{Guid.NewGuid():N}.txt");
        File.WriteAllText(textPath, request.Text);
        try
        {
            await SynthesisScratch.WriteThroughAsync(request.OutputPath, ".wav", async scratchPath =>
            {
                var result = await ProcessRunner.RunAsync(
                    executable,
                    new[] { "--model", model, "--output_file", scratchPath, "--input_file", textPath },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!result.Success)
                {
                    throw new SpeechSynthesisException("piper failed: " + ProcessResult.Tail(result.Combined, 8));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            FileSystemUtil.TryDelete(textPath);
        }
    }
}
