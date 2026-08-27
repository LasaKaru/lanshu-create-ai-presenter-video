using System.Runtime.InteropServices;

namespace Lanshu.Presenter.Core.Environment;

/// <summary>
/// Finds external tools without assuming a system install. Search order keeps a
/// portable copy shipped next to the .exe ahead of anything on PATH.
/// </summary>
public static class ToolLocator
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string ExecutableName(string stem) => IsWindows ? stem + ".exe" : stem;

    public static string AppDirectory
    {
        get
        {
            var baseDirectory = AppContext.BaseDirectory;
            // A single-file host extracts to a temp dir; prefer the real .exe location.
            var processPath = System.Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                var directory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    return directory;
                }
            }

            return baseDirectory;
        }
    }

    /// <summary>The folder this release keeps its data in.</summary>
    private const string DataDirectoryName = ".helapresenter";

    /// <summary>The folder releases before the HelaPresenter rename used.</summary>
    private const string LegacyDataDirectoryName = ".lanshu-presenter";

    /// <summary>
    /// Per-user home for downloaded tools, settings and the default workspace.
    ///
    /// A rename must not strand anyone. If the current folder does not exist yet but the one older
    /// builds wrote to does, that older folder is what gets used — a machine with saved keys, a
    /// downloaded FFmpeg and a year of jobs in it carries on working instead of starting empty.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var overridePath = System.Environment.GetEnvironmentVariable("HELA_HOME")
                               ?? System.Environment.GetEnvironmentVariable("LANSHU_HOME");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }

            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = AppDirectory;
            }

            return ResolveDataDirectory(home);
        }
    }

    internal static string ResolveDataDirectory(string home)
    {
        var current = Path.Combine(home, DataDirectoryName);
        if (Directory.Exists(current))
        {
            return current;
        }

        var legacy = Path.Combine(home, LegacyDataDirectoryName);
        return Directory.Exists(legacy) ? legacy : current;
    }

    public static string ToolsDirectory => Path.Combine(DataDirectory, "tools");

    public static string DefaultWorkspace => Path.Combine(DataDirectory, "jobs");

    public static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(AppDirectory, "tools");
        yield return Path.Combine(AppDirectory, "ffmpeg", "bin");
        yield return AppDirectory;
        yield return ToolsDirectory;
        yield return Path.Combine(ToolsDirectory, "bin");

        if (IsWindows)
        {
            var programFiles = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return Path.Combine(programFiles, "ffmpeg", "bin");
            }

            var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
            }
        }
        else
        {
            yield return "/usr/local/bin";
            yield return "/usr/bin";
            yield return "/opt/homebrew/bin";
            yield return "/snap/bin";
        }
    }

    public static string? Find(string stem, string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var expanded = Util.FileSystemUtil.ExpandPath(explicitPath);
            if (File.Exists(expanded))
            {
                return expanded;
            }

            var inDirectory = Path.Combine(expanded, ExecutableName(stem));
            if (File.Exists(inDirectory))
            {
                return inDirectory;
            }
        }

        var executable = ExecutableName(stem);
        foreach (var directory in CandidateDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return FindOnPath(executable);
    }

    public static string? FindOnPath(string executable)
    {
        var pathValue = System.Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
