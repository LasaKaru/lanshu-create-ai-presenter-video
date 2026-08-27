using System.Text.Json;
using Lanshu.Presenter.Core.Environment;
using Lanshu.Presenter.Core.Models;
using Lanshu.Presenter.Core.Util;

namespace Lanshu.Presenter.Core.Configuration;

/// <summary>
/// Loads and saves settings plus the separate credential store. Secrets never enter
/// job manifests, QA reports, or anything the repository tracks.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _root;
    private readonly object _gate = new();
    private AppSettings? _cached;
    private Dictionary<string, string>? _secrets;

    public SettingsStore(string? rootDirectory = null)
    {
        _root = rootDirectory is null ? ToolLocator.DataDirectory : Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory => _root;

    public string SettingsFile => Path.Combine(_root, "settings.json");

    public string SecretsFile => Path.Combine(_root, "secrets.json");

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (File.Exists(SettingsFile))
            {
                try
                {
                    _cached = JobJson.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                }
                catch (JsonException)
                {
                    // A hand-edited settings file should not brick the app; fall back to defaults
                    // and keep the broken copy for the user to inspect.
                    var broken = SettingsFile + ".invalid";
                    FileSystemUtil.TryDelete(broken);
                    File.Copy(SettingsFile, broken);
                    _cached = new AppSettings();
                }
            }
            else
            {
                _cached = new AppSettings();
            }

            return _cached;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            FileSystemUtil.WriteAtomic(SettingsFile, JobJson.Serialize(settings));
            _cached = settings;
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            _cached = null;
            _secrets = null;
        }
    }

    /// <summary>
    /// Resolution order: process environment first (so CI and shells win), then the local
    /// secrets file. Returns empty when the key is unknown.
    /// </summary>
    public string GetSecret(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var fromEnvironment = System.Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        lock (_gate)
        {
            _secrets ??= LoadSecrets();
            return _secrets.TryGetValue(key, out var value) ? value : string.Empty;
        }
    }

    public bool HasSecret(string key) => !string.IsNullOrWhiteSpace(GetSecret(key));

    public IReadOnlyList<string> SecretNames()
    {
        lock (_gate)
        {
            _secrets ??= LoadSecrets();
            return _secrets.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
        }
    }

    public void SetSecret(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_gate)
        {
            _secrets ??= LoadSecrets();
            if (string.IsNullOrWhiteSpace(value))
            {
                _secrets.Remove(key);
            }
            else
            {
                _secrets[key] = value.Trim();
            }

            Directory.CreateDirectory(_root);
            FileSystemUtil.WriteAtomic(SecretsFile, JobJson.Serialize(_secrets));
            RestrictPermissions(SecretsFile);
        }
    }

    private Dictionary<string, string> LoadSecrets()
    {
        if (!File.Exists(SecretsFile))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                       File.ReadAllText(SecretsFile),
                       JobJson.Options)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void RestrictPermissions(string path)
    {
        if (ToolLocator.IsWindows)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // Best effort on filesystems that do not carry unix modes.
        }
    }

    /// <summary>Known credential names, surfaced in the UI so users know what to fill in.</summary>
    public static readonly IReadOnlyList<SecretDescriptor> KnownSecrets = new[]
    {
        new SecretDescriptor("ANTHROPIC_API_KEY", "Anthropic", "Script writing with Claude models."),
        new SecretDescriptor("OPENAI_API_KEY", "OpenAI", "Script writing, text-to-speech, and Whisper transcription."),
        new SecretDescriptor("ELEVENLABS_API_KEY", "ElevenLabs", "Text-to-speech and authorized voice cloning."),
        new SecretDescriptor("AZURE_SPEECH_KEY", "Azure Speech", "Neural text-to-speech. Set the region in Settings."),
        new SecretDescriptor("PRESENTER_API_KEY", "Talking-head provider", "Whatever API key your presenter video endpoint expects."),
        new SecretDescriptor("LIPSYNC_API_KEY", "Lip-sync provider", "API key for the lip-sync repair endpoint."),
    };
}

public sealed record SecretDescriptor(string Key, string DisplayName, string Purpose);
