using System.Text;
using System.Text.RegularExpressions;

namespace Lanshu.Presenter.Core.Util;

public static class FileSystemUtil
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes through a sibling temp file so an interrupted run never leaves a half manifest.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, Utf8NoBom);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(temporary, path);
    }

    public static void WriteAllTextUtf8(string path, string text)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, text, Utf8NoBom);
    }

    public static string ResolveExisting(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{label} is required", nameof(value));
        }

        var full = ExpandPath(value);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"{label} does not exist or is not a file: {full}", full);
        }

        return full;
    }

    public static string ExpandPath(string value)
    {
        var text = value.Trim().Trim('"');
        if (text.StartsWith("~", StringComparison.Ordinal))
        {
            var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            text = Path.Combine(home, text.TrimStart('~').TrimStart('/', '\\'));
        }

        return Path.GetFullPath(text);
    }

    public static bool IsDirectoryEmpty(string path) =>
        !Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any();

    public static string Slugify(string value)
    {
        var slug = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "presenter-video" : slug;
    }

    /// <summary>Rejects stems that would escape the output directory or confuse a shell.</summary>
    public static bool IsSafeStem(string stem) =>
        !string.IsNullOrWhiteSpace(stem) && Regex.IsMatch(stem, "^[A-Za-z0-9._-]+$");

    public static void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Deletes a file if present; used to keep temp scratch tidy between attempts.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A locked scratch file is not worth failing the run over.
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
