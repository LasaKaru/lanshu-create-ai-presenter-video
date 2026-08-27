using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Environment;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("1.1.0", "1.0.0")]
    [InlineData("v1.1.0", "1.0.0")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.0.1", "1.0.0")]
    public void RecognizesANewerRelease(string candidate, string current)
    {
        Assert.True(UpdateChecker.IsNewer(candidate, current));
    }

    [Fact]
    public void ComparesNumericallyRatherThanAsText()
    {
        // A string comparison calls 1.10.0 older than 1.9.0, which is exactly the release where
        // somebody would notice.
        Assert.True(UpdateChecker.IsNewer("1.10.0", "1.9.0"));
        Assert.False(UpdateChecker.IsNewer("1.9.0", "1.10.0"));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("v1.0.0", "1.0.0")]
    public void DoesNotClaimAnUpdateForTheSameOrOlderVersion(string candidate, string current)
    {
        Assert.False(UpdateChecker.IsNewer(candidate, current));
    }

    [Fact]
    public void TreatsAShorterVersionAsZeroPadded()
    {
        Assert.True(UpdateChecker.IsNewer("1.1", "1.0.9"));
        Assert.False(UpdateChecker.IsNewer("1.0", "1.0.0"));
    }

    [Fact]
    public void IgnoresAPreReleaseSuffixWhenComparing()
    {
        Assert.True(UpdateChecker.IsNewer("1.1.0-beta.2", "1.0.0"));
        Assert.False(UpdateChecker.IsNewer("1.0.0+build7", "1.0.0"));
    }

    [Theory]
    [InlineData("nightly", "1.0.0")]
    [InlineData("1.0.0", "dev")]
    [InlineData("", "1.0.0")]
    public void AVersionItCannotParseIsNeverGroundsToTellSomeoneToUpgrade(string candidate, string current)
    {
        Assert.False(UpdateChecker.IsNewer(candidate, current));
    }
}

public class DiagnosticsRedactionTests
{
    [Theory]
    [InlineData("api_key")]
    [InlineData("secret_key")]
    [InlineData("auth_token")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("password")]
    [InlineData("client-secret")]
    [InlineData("authorization")]
    [InlineData("apikey")]
    public void RecognizesACredentialFieldName(string name)
    {
        Assert.True(DiagnosticsBundle.LooksSecret(name));
    }

    [Theory]
    [InlineData("max_output_tokens")]
    [InlineData("keyword_callouts_enabled")]
    [InlineData("monkey")]
    [InlineData("workspace")]
    [InlineData("poll_interval_s")]
    public void LeavesFieldsThatMerelyContainTheLettersAlone(string name)
    {
        // Redacting max_output_tokens hides a number a maintainer wants to see and protects
        // nothing, so "key" and "token" only count as a whole name segment.
        Assert.False(DiagnosticsBundle.LooksSecret(name));
    }

    [Fact]
    public void ReplacesCredentialValuesAnywhereInTheTree()
    {
        var node = JsonNode.Parse("""
        {
          "workspace": "/jobs",
          "script": { "max_output_tokens": 4000, "api_key": "sk-live-secret" },
          "providers": [ { "name": "one", "auth_token": "tok-secret" } ]
        }
        """);

        DiagnosticsBundle.Redact(node);
        var json = node!.ToJsonString();

        Assert.DoesNotContain("sk-live-secret", json);
        Assert.DoesNotContain("tok-secret", json);
        Assert.Contains("[redacted]", json);
        // The useful values survive.
        Assert.Contains("4000", json);
        Assert.Contains("/jobs", json);
        Assert.Contains("one", json);
    }

    [Fact]
    public void AnEmptyCredentialStaysEmptyRatherThanLookingSet()
    {
        var node = JsonNode.Parse("""{ "api_key": "" }""");

        DiagnosticsBundle.Redact(node);

        // Writing [redacted] over an empty value would make a missing key look configured.
        Assert.Equal(string.Empty, node!["api_key"]!.GetValue<string>());
    }

    [Fact]
    public void TheLogTailStartsOnALineBoundary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lanshu-tail-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, 500).Select(index => $"line {index:000}")));

        try
        {
            var tail = DiagnosticsBundle.ReadTail(path, 200);

            // A tail that begins mid-line reads as a corrupt file.
            Assert.DoesNotContain("\nline", tail[..Math.Min(6, tail.Length)]);
            Assert.StartsWith("line ", tail);
            Assert.EndsWith("line 499", tail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AShortLogComesBackWhole()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lanshu-tail-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, "only line");

        try
        {
            Assert.Equal("only line", DiagnosticsBundle.ReadTail(path, 4096));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingLogIsReportedRatherThanThrown()
    {
        var text = DiagnosticsBundle.ReadTail(Path.Combine(Path.GetTempPath(), "no-such-lanshu.log"), 1024);

        Assert.Contains("could not be read", text);
    }
}

public class DataDirectoryTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), $"hela-home-{Guid.NewGuid():N}");

    public DataDirectoryTests() => Directory.CreateDirectory(_home);

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Current => Path.Combine(_home, ".helapresenter");

    private string Legacy => Path.Combine(_home, ".lanshu-presenter");

    [Fact]
    public void AFreshMachineGetsTheCurrentFolder()
    {
        Assert.Equal(Current, ToolLocator.ResolveDataDirectory(_home));
    }

    [Fact]
    public void AMachineFromBeforeTheRenameKeepsUsingWhatItAlreadyHas()
    {
        Directory.CreateDirectory(Legacy);

        // Someone with saved keys, a downloaded FFmpeg and a year of jobs under the old name must
        // not open the new build to an empty workspace.
        Assert.Equal(Legacy, ToolLocator.ResolveDataDirectory(_home));
    }

    [Fact]
    public void OnceTheCurrentFolderExistsItWins()
    {
        Directory.CreateDirectory(Legacy);
        Directory.CreateDirectory(Current);

        Assert.Equal(Current, ToolLocator.ResolveDataDirectory(_home));
    }
}
