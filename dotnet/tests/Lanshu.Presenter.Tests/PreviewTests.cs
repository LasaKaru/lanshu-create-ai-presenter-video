using Lanshu.Presenter.Core.Pipeline;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class PreviewTests
{
    // The short edge is the smaller dimension whichever way the frame is oriented, so a 9:16
    // delivery scales by its width and a 16:9 delivery scales by its height.
    [Theory]
    [InlineData(1080, 1920, 640, 640, 1138)]
    [InlineData(1920, 1080, 640, 1138, 640)]
    [InlineData(1080, 1080, 640, 640, 640)]
    [InlineData(1080, 1350, 640, 640, 800)]
    public void ProxyKeepsTheAspectAndShrinksTheShortEdge(
        int width, int height, int target, int expectedWidth, int expectedHeight)
    {
        var (proxyWidth, proxyHeight) = PresenterVideoPipeline.ProxyDimensions(width, height, target);

        Assert.Equal(expectedWidth, proxyWidth);
        Assert.Equal(expectedHeight, proxyHeight);

        // H.264 rejects odd dimensions.
        Assert.Equal(0, proxyWidth % 2);
        Assert.Equal(0, proxyHeight % 2);

        var sourceAspect = (double)width / height;
        var proxyAspect = (double)proxyWidth / proxyHeight;
        Assert.True(Math.Abs(sourceAspect - proxyAspect) < 0.01, "the proxy must keep the delivery aspect");
    }

    [Fact]
    public void ProxyNeverUpscalesPastTheSource()
    {
        var (width, height) = PresenterVideoPipeline.ProxyDimensions(480, 640, 1080);
        Assert.Equal(480, width);
        Assert.Equal(640, height);
    }

    [Fact]
    public void ProxyClampsAnAbsurdRequest()
    {
        var (width, height) = PresenterVideoPipeline.ProxyDimensions(1080, 1920, 1);
        Assert.True(width >= 2 && height >= 2);
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }
}
