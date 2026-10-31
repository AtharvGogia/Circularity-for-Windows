using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Circularity;
using Circularity.Engine;

namespace Circularity.UnitTests;

public class ResizeSpecTests
{
    [Theory]
    [InlineData(ResizeKind.Fit, 800, 600, 400, 300, 400, 300)]       // exact fit
    [InlineData(ResizeKind.Fit, 800, 600, 1000, 1000, 1000, 750)]      // upscale - fits within, keeps ratio (scale 1.25)
    [InlineData(ResizeKind.Fit, 800, 600, 400, 400, 400, 300)]        // wider than tall target
    [InlineData(ResizeKind.Fit, 600, 800, 400, 400, 300, 400)]        // taller than wide target
    [InlineData(ResizeKind.Fit, 100, 100, 50, 50, 50, 50)]            // downscale
    [InlineData(ResizeKind.Stretch, 800, 600, 400, 300, 400, 300)]    // exact stretch
    [InlineData(ResizeKind.Stretch, 800, 600, 400, 400, 400, 400)]    // stretch ignores ratio
    [InlineData(ResizeKind.Stretch, 100, 100, 200, 150, 200, 150)]    // stretch upscale
    public void Apply_ReturnsExpectedSize(ResizeKind kind, int srcW, int srcH, int targetW, int targetH, int expectedW, int expectedH)
    {
        var spec = new ResizeSpec(kind, targetW, targetH);
        var (w, h) = spec.Apply(srcW, srcH);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Theory]
    [InlineData(ResizeKind.Fit, 800, 600, 400, 300, 400, 300)]       // exact fit
    [InlineData(ResizeKind.Fit, 800, 600, 1000, 1000, 1000, 750)]      // upscale - fits within
    [InlineData(ResizeKind.Fit, 800, 600, 400, 400, 400, 300)]        // wider than tall target
    [InlineData(ResizeKind.Fit, 600, 800, 400, 400, 300, 400)]        // taller than wide target
    [InlineData(ResizeKind.Stretch, 800, 600, 400, 300, 400, 300)]    // exact stretch
    [InlineData(ResizeKind.Stretch, 800, 600, 400, 400, 400, 400)]    // stretch ignores ratio
#pragma warning disable xUnit1026 // Theory method does not use parameter
    public void FittedSize_ReturnsSameAsApply_ForFitAndStretch(ResizeKind kind, int srcW, int srcH, int targetW, int targetH, int expectedW, int expectedH)
#pragma warning restore xUnit1026
    {
        var spec = new ResizeSpec(kind, targetW, targetH, ImageScale: 1.0);
        var (w1, h1) = spec.Apply(srcW, srcH);
        var (w2, h2) = spec.FittedSize(srcW, srcH);
        Assert.Equal(w1, w2);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void FittedSize_ReturnsUnscaledSize_ForFitWithBackground()
    {
        // FitWithBackground: FittedSize should not include ImageScale
        var spec = new ResizeSpec(ResizeKind.FitWithBackground, 800, 600, ImageScale: 0.5);
        var (w1, h1) = spec.Apply(400, 300);     // applies scale
        var (w2, h2) = spec.FittedSize(400, 300); // doesn't apply scale
        Assert.Equal(800, w2); // 400 * 2.0 (scale = min(800/400, 600/300) = 2.0)
        Assert.Equal(600, h2);
    }
}

public class CompressSpecTests
{
    [Fact]
    public void QualityOnly_Defaults()
    {
        var spec = new CompressSpec(Quality: 80);
        Assert.Equal(80, spec.Quality);
        Assert.Null(spec.MaxBytes);
        Assert.Null(spec.MaxHeight);
    }

    [Fact]
    public void TargetSizeOnly_Defaults()
    {
        var spec = new CompressSpec(MaxBytes: 1_000_000);
        Assert.Null(spec.Quality);
        Assert.Equal(1_000_000, spec.MaxBytes);
        Assert.Null(spec.MaxHeight);
    }

    [Fact]
    public void VideoWithMaxHeight()
    {
        var spec = new CompressSpec(Quality: 60, MaxHeight: 720);
        Assert.Equal(60, spec.Quality);
        Assert.Equal(720, spec.MaxHeight);
    }
}

public class ResizeBackgroundTests
{
    [Fact]
    public void Solid_CreatesCorrectBackground()
    {
        var bg = ResizeBackground.Solid(System.Windows.Media.Color.FromRgb(255, 0, 0));
        Assert.Equal(ResizeBackgroundKind.Color, bg.Kind);
        Assert.Equal(System.Windows.Media.Color.FromRgb(255, 0, 0), bg.Color);
        Assert.Null(bg.GradientStart);
        Assert.Null(bg.GradientEnd);
        Assert.Equal(0.0, bg.GradientAngle);
    }

    [Fact]
    public void Gradient_CreatesCorrectBackground_WithDirection()
    {
        var bg = ResizeBackground.Gradient(
            System.Windows.Media.Color.FromRgb(255, 0, 0),
            System.Windows.Media.Color.FromRgb(0, 0, 255),
            GradientDirection.Vertical);
        Assert.Equal(ResizeBackgroundKind.Gradient, bg.Kind);
        Assert.Equal(GradientDirection.Vertical, bg.GradientDirection);
        Assert.Equal(0.0, bg.GradientAngle);
    }

    [Fact]
    public void Gradient_CreatesCorrectBackground_WithAngle()
    {
        var bg = ResizeBackground.Gradient(
            System.Windows.Media.Color.FromRgb(255, 0, 0),
            System.Windows.Media.Color.FromRgb(0, 0, 255),
            45.0);
        Assert.Equal(ResizeBackgroundKind.Gradient, bg.Kind);
        Assert.Equal(45.0, bg.GradientAngle);
        Assert.Equal(GradientDirection.Horizontal, bg.GradientDirection); // legacy default
    }

    [Fact]
    public void Blurred_CreatesCorrectBackground()
    {
        var bg = ResizeBackground.Blurred(75.0, 1.5);
        Assert.Equal(ResizeBackgroundKind.BlurredSource, bg.Kind);
        Assert.Equal(75.0, bg.BlurRadius);
        Assert.Equal(1.5, bg.Scale);
    }
}