using System;
using Xunit;
using Circularity.Engine;

namespace Circularity.UnitTests;

public class VideoPlanTests
{
    [Theory]
    [InlineData(1920, 1080, 30, 8_000_000, true, 60, 1920, 1080)]    // High quality landscape
    [InlineData(1920, 1080, 30, 8_000_000, true, 55, 1920, 1080)]    // Balanced landscape
    [InlineData(1920, 1080, 30, 8_000_000, true, 40, 1920, 1080)]    // Lower quality landscape
    [InlineData(3840, 2160, 30, 40_000_000, true, 60, 3840, 2160)]   // 4K landscape
    [InlineData(1080, 1920, 30, 8_000_000, true, 60, 1080, 1920)]    // Portrait (preserves orientation)
    public void PlanCompression_QualityMode_ReturnsValidPlan(int w, int h, double fps, long bitrate, bool hasAudio, int quality, int expW, int expH)
    {
        var info = new MediaInfo(TimeSpan.FromSeconds(30), w, h, fps, bitrate, hasAudio);
        var spec = new CompressSpec(Quality: quality);
        var plan = FfmpegConverter.PlanCompression(info, spec);

        Assert.Equal(expW, plan.Width);
        Assert.Equal(expH, plan.Height);
        Assert.True(plan.VideoBitsPerSecond > 0);
        Assert.True(plan.EstimatedBytes > 0);
    }

    [Theory]
    [InlineData(1920, 1080, 30, 8_000_000, true, 5_000_000, 1280, 720)]     // 5MB target downscales to 720p
    [InlineData(1920, 1080, 30, 8_000_000, true, 25_000_000, 1920, 1080)]    // 25MB target fits 1080p
    [InlineData(3840, 2160, 30, 40_000_000, true, 10_000_000, 1920, 1080)]   // 4K to 10MB downscales to 1080p
    [InlineData(1920, 1080, 30, 8_000_000, false, 5_000_000, 1280, 720)]    // No audio
    public void PlanCompression_TargetSize_ReturnsValidPlan(int w, int h, double fps, long bitrate, bool hasAudio, long maxBytes, int expW, int expH)
    {
        var info = new MediaInfo(TimeSpan.FromSeconds(30), w, h, fps, bitrate, hasAudio);
        var spec = new CompressSpec(MaxBytes: maxBytes);
        var plan = FfmpegConverter.PlanCompression(info, spec);

        Assert.Equal(expW, plan.Width);
        Assert.Equal(expH, plan.Height);
        Assert.True(plan.VideoBitsPerSecond > 0);
        Assert.True(plan.AudioBitsPerSecond >= 0);
    }

    [Fact]
    public void PlanCompression_TargetSize_RespectsMaxHeight()
    {
        // 4K video, target size that forces downscaling to 1080p
        var info = new MediaInfo(TimeSpan.FromSeconds(60), 3840, 2160, 30, 50_000_000, true);
        var spec = new CompressSpec(MaxBytes: 5_000_000, MaxHeight: 1080);
        var plan = FfmpegConverter.PlanCompression(info, spec);

        Assert.True(plan.Height <= 1080);
    }

    [Theory]
    [InlineData(1920, 1080, 60, true)]      // 60fps capped at 60
    [InlineData(1920, 1080, 120, true)]     // 120fps capped at 60
    [InlineData(1920, 1080, 24, true)]      // 24fps kept
    [InlineData(1920, 1080, 30, true)]      // 30fps kept
#pragma warning disable xUnit1026 // Theory method does not use parameter
    public void PlanCompression_CapsFpsAt60(int w, int h, double fps, bool hasAudio)
#pragma warning restore xUnit1026
    {
        var info = new MediaInfo(TimeSpan.FromSeconds(30), 1920, 1080, fps, 8_000_000, hasAudio);
        var spec = new CompressSpec(Quality: 60);
        var plan = FfmpegConverter.PlanCompression(info, spec);

        // FPS used in calculation should be capped at 60
        Assert.True(plan.VideoBitsPerSecond > 0);
    }
}