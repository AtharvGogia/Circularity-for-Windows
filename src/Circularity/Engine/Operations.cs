using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Circularity.Engine;

/// <summary>What to do to a file: convert, resize, crop, compress, trim or combine it.</summary>
internal abstract record Operation
{
    /// <summary>Added to the output file name, e.g. "photo (resized).jpg". Null for plain conversions.</summary>
    public abstract string? NameSuffix { get; }

    /// <summary>Short label for progress and notifications.</summary>
    public abstract string Describe();
}

internal sealed record ConvertTo(string Target) : Operation
{
    public override string? NameSuffix => null;
    public override string Describe() => Target.ToUpperInvariant();
}

internal sealed record ResizeTo(ResizeSpec Spec) : Operation
{
    public override string? NameSuffix => "resized";
    public override string Describe() => "Resize";
}

internal sealed record CropTo(PixelRect Rect) : Operation
{
    public override string? NameSuffix => "cropped";
    public override string Describe() => "Crop";
}

/// <summary>
/// Puts several images and/or PDFs, in order, into one PDF. Queued as a single job on the first file,
/// so the result is named after it ("scan (combined).pdf").
/// </summary>
internal sealed record CombineIntoPdf(IReadOnlyList<string> Files) : Operation
{
    public override string? NameSuffix => "combined";
    public override string Describe() => "Combine";
}

internal sealed record CompressTo(CompressSpec Spec) : Operation
{
    public override string? NameSuffix => "compressed";
    public override string Describe() => "Compress";
}

/// <summary>
/// Either a quality from 1 to 100, or the largest file size allowed, in bytes. For images the quality
/// is the JPEG quality; for videos and PDFs it picks how hard to squeeze (see the dialogs). Videos can
/// also be scaled down to at most <paramref name="MaxHeight"/> pixels tall.
/// </summary>
internal sealed record CompressSpec(int? Quality = null, long? MaxBytes = null, int? MaxHeight = null);

/// <summary>Keeps the part of a video or audio file between two times.</summary>
/// <param name="Exact">
/// Re-encode, so the cut lands exactly on the chosen times. Otherwise the streams are copied as they
/// are: instant and lossless, but a video can only start on a keyframe, so it may begin a little early.
/// </param>
internal sealed record TrimTo(TimeSpan Start, TimeSpan End, bool Exact) : Operation
{
    public override string? NameSuffix => "trimmed";
    public override string Describe() => "Trim";
}

internal enum ResizeKind
{
    /// <summary>Largest size that fits inside Width × Height, keeping the aspect ratio.</summary>
    Fit,

    /// <summary>Exactly Width × Height, ignoring the aspect ratio.</summary>
    Stretch,

    /// <summary>Fits inside Width × Height keeping aspect ratio, then fills remaining canvas with a background.</summary>
    FitWithBackground,
}

/// <summary>Background fill mode for FitWithBackground.</summary>
internal enum ResizeBackgroundKind
{
    /// <summary>Solid color fill.</summary>
    Color,

    /// <summary>Linear gradient fill.</summary>
    Gradient,

    /// <summary>Blurred copy of the source image.</summary>
    BlurredSource,
}

/// <summary>Gradient direction for background (legacy, kept for compatibility).</summary>
internal enum GradientDirection
{
    Horizontal,
    Vertical,
    DiagonalUp,
    DiagonalDown,
}

/// <summary>Describes how to fill the canvas background when the resized image doesn't fill it.</summary>
internal sealed record ResizeBackground(
    ResizeBackgroundKind Kind = ResizeBackgroundKind.Color,
    Color? Color = null,
    Color? GradientStart = null,
    Color? GradientEnd = null,
    GradientDirection GradientDirection = GradientDirection.Horizontal,
    double GradientAngle = 0, // Angle in degrees (0-359), overrides GradientDirection when non-zero
    double BlurRadius = 50,
    double Scale = 1.0) // Scale of the fitted image within the canvas (0.1 to 2.0)
{
    public static ResizeBackground Solid(Color color) => new(ResizeBackgroundKind.Color, Color: color);
    public static ResizeBackground Gradient(Color start, Color end, GradientDirection dir = GradientDirection.Horizontal) =>
        new(ResizeBackgroundKind.Gradient, GradientStart: start, GradientEnd: end, GradientDirection: dir);
    public static ResizeBackground Gradient(Color start, Color end, double angle) =>
        new(ResizeBackgroundKind.Gradient, GradientStart: start, GradientEnd: end, GradientAngle: angle);
    public static ResizeBackground Blurred(double radius = 50, double scale = 1.0) =>
        new(ResizeBackgroundKind.BlurredSource, BlurRadius: radius, Scale: scale);
}

internal sealed record ResizeSpec(ResizeKind Kind, int Width, int Height, ResizeBackground? Background = null, double ImageScale = 1.0)
{
    /// <summary>The new size for a source of the given size.</summary>
    public (int Width, int Height) Apply(int width, int height)
    {
        if (Kind == ResizeKind.Stretch)
            return (Math.Max(1, Width), Math.Max(1, Height));

        double scale = Math.Min((double)Width / width, (double)Height / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>Gets the fitted image size (before ImageScale is applied).</summary>
    public (int Width, int Height) FittedSize(int width, int height)
    {
        if (Kind == ResizeKind.Stretch)
            return (Math.Max(1, Width), Math.Max(1, Height));

        double scale = Math.Min((double)Width / width, (double)Height / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}

/// <summary>A rectangle in source pixels (after any EXIF or video rotation has been applied).</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height);
