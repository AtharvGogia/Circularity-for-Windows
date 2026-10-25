using System;
using System.Collections.Generic;

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
}

internal sealed record ResizeSpec(ResizeKind Kind, int Width, int Height)
{
    /// <summary>The new size for a source of the given size.</summary>
    public (int Width, int Height) Apply(int width, int height)
    {
        if (Kind == ResizeKind.Stretch)
            return (Math.Max(1, Width), Math.Max(1, Height));

        double scale = Math.Min((double)Width / width, (double)Height / height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}

/// <summary>A rectangle in source pixels (after any EXIF or video rotation has been applied).</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height);
