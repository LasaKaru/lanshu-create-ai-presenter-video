using Lanshu.Presenter.Core.Configuration;
using Lanshu.Presenter.Core.Models;

namespace Lanshu.Presenter.Core.Branding;

/// <summary>
/// Stores named brand kits alongside the rest of the settings and applies one to a job.
/// Kits live in settings rather than in a job folder because the whole point is that they
/// outlive any single video.
/// </summary>
public sealed class BrandKitService
{
    private readonly SettingsStore _store;

    public BrandKitService(SettingsStore store)
    {
        _store = store;
    }

    public IReadOnlyList<BrandKit> List() => _store.Load().BrandKits
        .OrderBy(kit => kit.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public BrandKit? Find(string name) => string.IsNullOrWhiteSpace(name)
        ? null
        : _store.Load().BrandKits
            .FirstOrDefault(kit => string.Equals(kit.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or replaces a kit by name. Returns true when an existing kit was replaced.</summary>
    public bool Save(BrandKit kit)
    {
        if (string.IsNullOrWhiteSpace(kit.Name))
        {
            throw new ArgumentException("a brand kit needs a name", nameof(kit));
        }

        var settings = _store.Load();
        var existing = settings.BrandKits
            .FindIndex(candidate => string.Equals(candidate.Name, kit.Name, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            settings.BrandKits[existing] = kit;
        }
        else
        {
            settings.BrandKits.Add(kit);
        }

        _store.Save(settings);
        return existing >= 0;
    }

    public bool Delete(string name)
    {
        var settings = _store.Load();
        var removed = settings.BrandKits
            .RemoveAll(kit => string.Equals(kit.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            _store.Save(settings);
        }

        return removed > 0;
    }

    /// <summary>
    /// Captures a job's current look as a kit, so a video that was tuned by hand can become the
    /// template for the next one without retyping any of it.
    /// </summary>
    public static BrandKit FromCreative(string name, JobCreative creative) => new()
    {
        Name = name,
        AccentColor = creative.AccentColor,
        CaptionFont = creative.CaptionFont,
        CaptionStyle = creative.CaptionStyle,
        Watermark = creative.Watermark,
        MusicPath = creative.MusicPath,
        MusicGainDb = creative.MusicGainDb,
        Intro = creative.Intro.Clone(),
        Outro = creative.Outro.Clone(),
    };

    /// <summary>Applies a kit by name, or reports that no such kit exists.</summary>
    public bool Apply(string name, JobCreative creative)
    {
        var kit = Find(name);
        if (kit is null)
        {
            return false;
        }

        kit.ApplyTo(creative);
        return true;
    }
}
