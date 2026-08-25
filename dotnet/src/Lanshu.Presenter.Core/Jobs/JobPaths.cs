namespace Lanshu.Presenter.Core.Jobs;

/// <summary>
/// The required job artifact tree from SKILL.md, resolved against one job directory.
/// </summary>
public sealed class JobPaths
{
    public JobPaths(string jobDirectory)
    {
        Root = Path.GetFullPath(jobDirectory);
    }

    public string Root { get; }

    public string ManifestFile => Path.Combine(Root, "job.json");

    public string Docs => Path.Combine(Root, "docs");

    public string SourceAssets => Path.Combine(Root, "assets", "source");

    public string AudioReference => Path.Combine(Root, "assets", "audio", "reference");

    public string AudioRaw => Path.Combine(Root, "assets", "audio", "raw");

    public string AudioFinal => Path.Combine(Root, "assets", "audio", "final");

    public string VideoCandidates => Path.Combine(Root, "assets", "video", "candidates");

    public string VideoSelected => Path.Combine(Root, "assets", "video", "selected");

    public string VideoRender => Path.Combine(Root, "assets", "video", "render");

    public string Captions => Path.Combine(Root, "assets", "captions");

    public string QaRequests => Path.Combine(Root, "qa", "requests");

    public string QaAsr => Path.Combine(Root, "qa", "asr");

    public string QaContacts => Path.Combine(Root, "qa", "contacts");

    public string QaReports => Path.Combine(Root, "qa", "reports");

    public string Renders => Path.Combine(Root, "renders");

    public string Outputs => Path.Combine(Root, "outputs");

    public string Temp => Path.Combine(Root, ".work");

    public string RunLog => Path.Combine(QaReports, "run-log.txt");

    public static readonly string[] RelativeDirectories =
    {
        "docs",
        "assets/source",
        "assets/audio/reference",
        "assets/audio/raw",
        "assets/audio/final",
        "assets/video/candidates",
        "assets/video/selected",
        "assets/video/render",
        "assets/captions",
        "qa/requests",
        "qa/asr",
        "qa/contacts",
        "qa/reports",
        "renders",
        "outputs",
    };

    public void CreateAll()
    {
        Directory.CreateDirectory(Root);
        foreach (var relative in RelativeDirectories)
        {
            Directory.CreateDirectory(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        Directory.CreateDirectory(Temp);
    }

    /// <summary>Job-relative path used inside reports so shared files never leak a machine path.</summary>
    public string Relative(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (!full.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(full);
        }

        return Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    public string Resolve(string relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(relativeOrAbsolute)
            ? Path.GetFullPath(relativeOrAbsolute)
            : Path.GetFullPath(Path.Combine(Root, relativeOrAbsolute));
    }
}
