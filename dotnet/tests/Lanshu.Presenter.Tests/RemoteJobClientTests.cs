using System.Text.Json.Nodes;
using Lanshu.Presenter.Core.Media;
using Lanshu.Presenter.Core.Presenter;
using Lanshu.Presenter.Core.Timeline;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class RemoteJobClientTests
{
    [Fact]
    public void ReadsDottedAndIndexedPaths()
    {
        var node = JsonNode.Parse("""
            {"id":"abc","status":"succeeded","output":{"video_url":"https://cdn/x.mp4"},
             "data":{"results":[{"url":"https://cdn/first.mp4"}]}}
            """);

        Assert.Equal("abc", RemoteJobClient.ReadPath(node, "id"));
        Assert.Equal("succeeded", RemoteJobClient.ReadPath(node, "status"));
        Assert.Equal("https://cdn/x.mp4", RemoteJobClient.ReadPath(node, "output.video_url"));
        Assert.Equal("https://cdn/first.mp4", RemoteJobClient.ReadPath(node, "data.results.0.url"));
        Assert.Null(RemoteJobClient.ReadPath(node, "output.missing"));
        Assert.Null(RemoteJobClient.ReadPath(node, "does.not.exist"));
    }

    [Fact]
    public void SanitizeStripsCredentialsAndEmbeddedMedia()
    {
        var body = """
            {
              "api_key": "sk-live-should-never-be-archived",
              "image": "data:image/png;base64,AAAABBBBCCCCDDDD",
              "callback": "https://cdn.example.com/upload?signature=secret-token",
              "prompt": "keep this",
              "nested": {"access_token": "another-secret"}
            }
            """;

        var sanitized = RemoteJobClient.Sanitize(body);

        Assert.DoesNotContain("sk-live-should-never-be-archived", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAABBBBCCCCDDDD", sanitized, StringComparison.Ordinal);
        Assert.Contains("image/png omitted", sanitized, StringComparison.Ordinal);
        Assert.Contains("keep this", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonEscapeKeepsTemplatesValid()
    {
        var escaped = RemoteJobClient.JsonEscape("a \"quoted\" prompt\nwith a newline");
        var document = JsonNode.Parse($"{{\"prompt\":\"{escaped}\"}}");
        Assert.Equal("a \"quoted\" prompt\nwith a newline", document!["prompt"]!.GetValue<string>());
    }

    [Fact]
    public void MimeTypesCoverTheSupportedInputs()
    {
        Assert.Equal("image/png", RemoteJobClient.MimeFor("a.PNG"));
        Assert.Equal("audio/wav", RemoteJobClient.MimeFor("a.wav"));
        Assert.Equal("video/mp4", RemoteJobClient.MimeFor("a.mp4"));
        Assert.Equal("application/octet-stream", RemoteJobClient.MimeFor("a.unknown"));
    }

    [Fact]
    public void LoudnessJsonIsReadFromTheTailOfFfmpegOutput()
    {
        var stderr = """
            [Parsed_loudnorm_0 @ 0x5] noise
            frame= 100 fps=0.0
            {
              "input_i" : "-21.50",
              "input_tp" : "-3.20",
              "target_offset" : "0.40"
            }
            """;

        var json = FfmpegService.ExtractTrailingJson(stderr);
        Assert.NotNull(json);
        Assert.Equal("-21.50", JsonNode.Parse(json!)!["input_i"]!.GetValue<string>());
        Assert.Null(FfmpegService.ExtractTrailingJson("no json here"));
    }

    [Fact]
    public void FfmpegColoursUseRgbOrder()
    {
        Assert.Equal("0xF4C430", FfmpegCompositor.ToFfmpegColor("#f4c430"));
        Assert.Equal("0xFF0000", FfmpegCompositor.ToFfmpegColor("#f00"));
        Assert.Equal("0xF4C430", FfmpegCompositor.ToFfmpegColor("not-a-colour"));
    }

    [Fact]
    public void FilterValuesEscapeWindowsPaths()
    {
        var escaped = FfmpegService.EscapeFilterValue(@"C:\Users\Me\fonts");
        Assert.Equal(@"C\:/Users/Me/fonts", escaped);
    }
}
