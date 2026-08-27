using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Configuration;

namespace Lanshu.Presenter.Core.Environment;

public sealed record UpdateStatus(
    bool Checked,
    bool UpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string Detail)
{
    public static UpdateStatus NotChecked(string current, string detail) =>
        new(false, false, current, string.Empty, string.Empty, detail);
}

/// <summary>
/// Asks GitHub whether a newer release exists.
///
/// Two rules shape this. It never blocks anything: a check that cannot reach the network, or that
/// hangs, must cost the user nothing, so it runs with its own short timeout and every failure is
/// an answer rather than an exception. And it is only ever a notification — nothing is downloaded
/// and nothing is replaced, because silently swapping an executable someone is running is not a
/// convenience, it is a surprise.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>Long enough for a normal reply, short enough that nobody notices a dead network.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    private readonly SettingsStore _store;
    private readonly HttpClient _httpClient;

    public UpdateChecker(SettingsStore store, HttpClient httpClient)
    {
        _store = store;
        _httpClient = httpClient;
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = EnvironmentService.AppVersion;
        var settings = _store.Load();

        if (!settings.CheckForUpdates)
        {
            return UpdateStatus.NotChecked(current, "update checks are switched off in Settings");
        }

        var feed = settings.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed))
        {
            return UpdateStatus.NotChecked(current, "no update feed is configured");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, feed);
            request.Headers.Add("Accept", "application/vnd.github+json");
            // GitHub rejects an unidentified client, and identifying honestly is the right thing.
            request.Headers.Add("User-Agent", $"helapresenter/{current}");

            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateStatus.NotChecked(current, $"the release feed answered {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var node = JsonNode.Parse(body);
            var tag = node?["tag_name"]?.GetValue<string>() ?? string.Empty;
            var url = node?["html_url"]?.GetValue<string>() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(tag))
            {
                return UpdateStatus.NotChecked(current, "the release feed named no version");
            }

            var newer = IsNewer(tag, current);
            return new UpdateStatus(
                true,
                newer,
                current,
                tag,
                url,
                newer ? $"{tag} is available" : "this is the newest release");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateStatus.NotChecked(current, "the release feed did not answer in time");
        }
        catch (Exception exception)
        {
            return UpdateStatus.NotChecked(current, $"the release feed could not be reached: {exception.Message}");
        }
    }

    /// <summary>
    /// Compares two versions numerically, part by part.
    ///
    /// A string comparison would call 1.10.0 older than 1.9.0, which is exactly the release where
    /// somebody would notice. Anything unparseable answers "not newer": a version this code does
    /// not understand is not grounds for telling someone to upgrade.
    /// </summary>
    internal static bool IsNewer(string candidate, string current)
    {
        var left = Parse(candidate);
        var right = Parse(current);

        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var a = index < left.Length ? left[index] : 0;
            var b = index < right.Length ? right[index] : 0;

            if (a != b)
            {
                return a > b;
            }
        }

        return false;
    }

    private static int[] Parse(string version)
    {
        var value = (version ?? string.Empty).Trim().TrimStart('v', 'V');

        // Drop any pre-release or build suffix: 1.2.0-beta.1 compares as 1.2.0.
        var cut = value.IndexOfAny(new[] { '-', '+' });
        if (cut > 0)
        {
            value = value[..cut];
        }

        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>(parts.Length);

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var number))
            {
                return Array.Empty<int>();
            }

            numbers.Add(number);
        }

        return numbers.ToArray();
    }
}
