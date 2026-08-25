using Lanshu.Presenter.Core.Media;
using Xunit;

namespace Lanshu.Presenter.Tests;

public class EncoderTests
{
    [Fact]
    public void EveryProfileEmitsItsOwnCodecForEveryQuality()
    {
        foreach (var profile in VideoEncoderProfile.All)
        {
            foreach (var quality in Enum.GetValues<EncodeQuality>())
            {
                var arguments = profile.Arguments(quality);
                Assert.Equal("-c:v", arguments[0]);
                Assert.Equal(profile.Name, arguments[1]);

                // Flags come in pairs after the codec, so an odd count means a missing value.
                Assert.True(arguments.Count % 2 == 0, $"{profile.Name}/{quality} has an unpaired flag");
            }
        }
    }

    [Fact]
    public void QualityFlagsAreEncoderAppropriate()
    {
        // A CRF means nothing to NVENC, and -cq means nothing to x264; mixing them silently
        // produces either an error or a wildly wrong bitrate.
        Assert.Contains("-crf", VideoEncoderProfile.Software.Arguments(EncodeQuality.Master));
        Assert.DoesNotContain("-crf", VideoEncoderProfile.Nvenc.Arguments(EncodeQuality.Master));
        Assert.Contains("-cq", VideoEncoderProfile.Nvenc.Arguments(EncodeQuality.Master));
        Assert.Contains("-global_quality", VideoEncoderProfile.Qsv.Arguments(EncodeQuality.Master));
        Assert.Contains("-q:v", VideoEncoderProfile.VideoToolbox.Arguments(EncodeQuality.Master));
        Assert.Contains("-quality", VideoEncoderProfile.Amf.Arguments(EncodeQuality.Master));
    }

    [Fact]
    public void PreviewIsAlwaysCheaperThanMaster()
    {
        var preview = VideoEncoderProfile.Software.Arguments(EncodeQuality.Preview);
        var master = VideoEncoderProfile.Software.Arguments(EncodeQuality.Master);

        Assert.Contains("ultrafast", preview);
        Assert.Contains("slow", master);

        var previewCrf = int.Parse(preview[preview.ToList().IndexOf("-crf") + 1]);
        var masterCrf = int.Parse(master[master.ToList().IndexOf("-crf") + 1]);
        Assert.True(previewCrf > masterCrf, "a higher CRF is a smaller, faster encode");
    }

    [Theory]
    [InlineData("software", "libx264")]
    [InlineData("x264", "libx264")]
    [InlineData("nvenc", "h264_nvenc")]
    [InlineData("nvidia", "h264_nvenc")]
    [InlineData("qsv", "h264_qsv")]
    [InlineData("intel", "h264_qsv")]
    [InlineData("amd", "h264_amf")]
    [InlineData("apple", "h264_videotoolbox")]
    [InlineData("videotoolbox", "h264_videotoolbox")]
    public void NamesResolveToProfiles(string input, string expected)
    {
        Assert.Equal(expected, VideoEncoderProfile.ByName(input)!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("av1")]
    [InlineData(null)]
    public void UnknownNamesResolveToNothing(string? input)
    {
        Assert.Null(VideoEncoderProfile.ByName(input));
    }

    [Fact]
    public void OnlySoftwareIsMarkedAsSoftware()
    {
        Assert.False(VideoEncoderProfile.Software.IsHardware);
        Assert.All(
            VideoEncoderProfile.All.Where(profile => profile.Name != "libx264"),
            profile => Assert.True(profile.IsHardware));
    }
}
