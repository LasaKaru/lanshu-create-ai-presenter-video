using System.IO.Compression;
using System.Runtime.InteropServices;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Environment;

/// <summary>
/// Downloads a portable FFmpeg build into the per-user tools directory so a fresh
/// Windows machine can render without any manual setup. Nothing is installed system wide.
/// </summary>
public sealed class FfmpegInstaller
{
    public const string DefaultWindowsUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public const string DefaultLinuxUrl =
        "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz";

    private readonly HttpClient _httpClient;

    public FfmpegInstaller(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    }

    public static string? DefaultDownloadUrl
    {
        get
        {
            if (ToolLocator.IsWindows)
            {
                return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? null : DefaultWindowsUrl;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return RuntimeInformation.OSArchitecture == Architecture.X64 ? DefaultLinuxUrl : null;
            }

            return null;
        }
    }

    public sealed record InstallResult(string FfmpegPath, string FfprobePath, string InstalledFrom);

    public async Task<InstallResult> InstallAsync(
        string? downloadUrl = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var url = downloadUrl ?? DefaultDownloadUrl
            ?? throw new ExternalToolException(
                "No portable FFmpeg build is published for this platform. Install FFmpeg manually and set its path in Settings.");

        var toolsDirectory = ToolLocator.ToolsDirectory;
        Directory.CreateDirectory(toolsDirectory);
        var stagingDirectory = Path.Combine(toolsDirectory, "_staging");
        FileSystemUtil.TryDeleteDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);

        var archiveName = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(archiveName))
        {
            archiveName = "ffmpeg-download";
        }

        var archivePath = Path.Combine(stagingDirectory, archiveName);

        try
        {
            progress?.Invoke($"Downloading FFmpeg from {url}");
            await DownloadAsync(url, archivePath, progress, cancellationToken).ConfigureAwait(false);

            progress?.Invoke("Extracting FFmpeg");
            var extractDirectory = Path.Combine(stagingDirectory, "extract");
            Directory.CreateDirectory(extractDirectory);
            await ExtractAsync(archivePath, extractDirectory, cancellationToken).ConfigureAwait(false);

            var ffmpegSource = FindExecutable(extractDirectory, "ffmpeg")
                ?? throw new ExternalToolException("the downloaded archive did not contain an ffmpeg binary");
            var ffprobeSource = FindExecutable(extractDirectory, "ffprobe")
                ?? throw new ExternalToolException("the downloaded archive did not contain an ffprobe binary");

            var binDirectory = Path.Combine(toolsDirectory, "bin");
            Directory.CreateDirectory(binDirectory);
            var ffmpegTarget = Path.Combine(binDirectory, ToolLocator.ExecutableName("ffmpeg"));
            var ffprobeTarget = Path.Combine(binDirectory, ToolLocator.ExecutableName("ffprobe"));

            File.Copy(ffmpegSource, ffmpegTarget, overwrite: true);
            File.Copy(ffprobeSource, ffprobeTarget, overwrite: true);
            MakeExecutable(ffmpegTarget);
            MakeExecutable(ffprobeTarget);

            progress?.Invoke($"FFmpeg installed to {binDirectory}");
            return new InstallResult(ffmpegTarget, ffprobeTarget, url);
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long written = 0;
        var lastReport = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (written - lastReport > 8L * 1024 * 1024)
            {
                lastReport = written;
                progress?.Invoke(total > 0
                    ? $"Downloaded {written / 1_048_576} MB of {total / 1_048_576} MB"
                    : $"Downloaded {written / 1_048_576} MB");
            }
        }
    }

    private static async Task ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        var tar = ToolLocator.FindOnPath(ToolLocator.ExecutableName("tar"))
            ?? throw new ExternalToolException("tar is required to extract this FFmpeg archive");
        var result = await ProcessRunner
            .RunAsync(tar, new[] { "-xf", archivePath, "-C", destination }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        result.EnsureSuccess("tar extraction");
    }

    private static string? FindExecutable(string root, string stem)
    {
        var name = ToolLocator.ExecutableName(stem);
        return Directory
            .EnumerateFiles(root, name, SearchOption.AllDirectories)
            .OrderBy(path => path.Length)
            .FirstOrDefault();
    }

    private static void MakeExecutable(string path)
    {
        if (ToolLocator.IsWindows)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception)
        {
            // Best effort: a read-only filesystem still leaves a usable copy elsewhere.
        }
    }
}
