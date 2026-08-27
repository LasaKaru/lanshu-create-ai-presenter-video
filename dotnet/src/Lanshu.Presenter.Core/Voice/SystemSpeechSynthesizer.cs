using System.Text;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Voice;

/// <summary>
/// Windows SAPI through PowerShell. This is what makes the app usable on a clean Windows
/// machine with no API key and no network: every installed system voice is available.
/// </summary>
public sealed class SystemSpeechSynthesizer : ISpeechSynthesizer
{
    private const string ListScript = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Speech
        $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
        foreach ($voice in $synth.GetInstalledVoices()) {
          if ($voice.Enabled) {
            $info = $voice.VoiceInfo
            Write-Output ("{0}`t{1}" -f $info.Name, $info.Culture.Name)
          }
        }
        $synth.Dispose()
        """;

    private const string SpeakScript = """
        param([string]$TextFile, [string]$OutFile, [string]$VoiceName, [int]$Rate)
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Speech
        $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
        if ($VoiceName) { try { $synth.SelectVoice($VoiceName) } catch { } }
        $synth.Rate = $Rate
        $synth.Volume = 100
        $synth.SetOutputToWaveFile($OutFile)
        $text = [System.IO.File]::ReadAllText($TextFile, [System.Text.Encoding]::UTF8)
        $synth.Speak($text)
        $synth.SetOutputToNull()
        $synth.Dispose()
        """;

    public string Provider => "system-sapi";

    public string Model => "windows-speech";

    public bool IsRemote => false;

    public static string? PowerShellPath =>
        ToolLocator.Find("powershell") ?? ToolLocator.Find("pwsh");

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ToolLocator.IsWindows && PowerShellPath is not null);

    public async Task<IReadOnlyList<VoiceDescriptor>> ListVoicesAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var scriptPath = WriteTempScript(ListScript);
        try
        {
            var result = await ProcessRunner.RunAsync(
                PowerShellPath!,
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                return Array.Empty<VoiceDescriptor>();
            }

            var voices = new List<VoiceDescriptor>();
            foreach (var line in result.StandardOutput.Replace("\r\n", "\n").Split('\n'))
            {
                var parts = line.Split('\t');
                if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                var name = parts[0].Trim();
                var culture = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                voices.Add(new VoiceDescriptor(name, name, culture, Provider));
            }

            return voices;
        }
        finally
        {
            FileSystemUtil.TryDelete(scriptPath);
        }
    }

    public async Task SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var powerShell = PowerShellPath
            ?? throw new SpeechSynthesisException("PowerShell is required for Windows system voices");

        var scriptPath = WriteTempScript(SpeakScript);
        var textPath = Path.Combine(Path.GetTempPath(), $"lanshu-tts-{Guid.NewGuid():N}.txt");
        File.WriteAllText(textPath, request.Text, new UTF8Encoding(false));

        try
        {
            // SAPI rate is an integer from -10 to 10 around the engine default of 0.
            var rate = Math.Clamp((int)Math.Round((request.Rate - 1.0) * 10), -10, 10);

            await SynthesisScratch.WriteThroughAsync(request.OutputPath, ".wav", async scratchPath =>
            {
                var result = await ProcessRunner.RunAsync(
                    powerShell,
                    new[]
                    {
                        "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", scriptPath,
                        "-TextFile", textPath,
                        "-OutFile", scratchPath,
                        "-VoiceName", request.VoiceId ?? string.Empty,
                        "-Rate", rate.ToString(),
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!result.Success)
                {
                    throw new SpeechSynthesisException(
                        "Windows speech synthesis failed: " + ProcessResult.Tail(result.Combined, 12));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            FileSystemUtil.TryDelete(scriptPath);
            FileSystemUtil.TryDelete(textPath);
        }
    }

    private static string WriteTempScript(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lanshu-tts-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return path;
    }
}
