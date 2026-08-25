using System.Diagnostics;
using System.Text;

namespace Lanshu.Presenter.Core.Util;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    public string Combined => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : StandardOutput + System.Environment.NewLine + StandardError;

    public void EnsureSuccess(string what)
    {
        if (!Success)
        {
            var detail = Tail(StandardError, 40);
            throw new ExternalToolException($"{what} failed with exit code {ExitCode}.{System.Environment.NewLine}{detail}");
        }
    }

    public static string Tail(string text, int lines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var all = text.Replace("\r\n", "\n").Split('\n');
        return string.Join(System.Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
    }
}

public sealed class ExternalToolException : Exception
{
    public ExternalToolException(string message) : base(message)
    {
    }

    public ExternalToolException(string message, Exception inner) : base(message, inner)
    {
    }
}

public static class ProcessRunner
{
    /// <summary>
    /// Runs an external tool with arguments passed as a list so nothing is re-parsed by a shell.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        Action<string>? onOutputLine = null,
        IDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            stdout.AppendLine(args.Data);
            onOutputLine?.Invoke(args.Data);
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            stderr.AppendLine(args.Data);
            onOutputLine?.Invoke(args.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new ExternalToolException($"could not start '{fileName}': {exception.Message}", exception);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // ffmpeg reads stdin by default and will stall on a closed console; give it an empty stream.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process already went away.
        }
    }
}
