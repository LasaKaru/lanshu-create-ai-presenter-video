namespace Lanshu.Presenter.Core.Voice;

/// <summary>
/// Runs a command-line speech engine against a short temp path and then moves the result into
/// place. Several engines (espeak-ng among them) hold the output filename in a fixed-size buffer
/// and silently truncate a long path, which produces a file nobody can find. Job directories
/// nest deeply, so every local engine writes through here.
/// </summary>
internal static class SynthesisScratch
{
    public static async Task WriteThroughAsync(
        string finalPath,
        string extension,
        Func<string, Task> synthesize)
    {
        var scratchDirectory = Path.Combine(Path.GetTempPath(), "lanshu-tts");
        Directory.CreateDirectory(scratchDirectory);
        var scratchPath = Path.Combine(scratchDirectory, $"s{Guid.NewGuid():N}"[..12] + extension);

        try
        {
            await synthesize(scratchPath).ConfigureAwait(false);

            if (!File.Exists(scratchPath) || new FileInfo(scratchPath).Length == 0)
            {
                throw new SpeechSynthesisException(
                    "the speech engine reported success but wrote no audio");
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(finalPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(scratchPath, finalPath, overwrite: true);
        }
        finally
        {
            Util.FileSystemUtil.TryDelete(scratchPath);
        }
    }
}
