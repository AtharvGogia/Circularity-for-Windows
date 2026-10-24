using System;

namespace GestureSpike.Engine;

/// <summary>What to do to a file: convert it, resize it or crop it.</summary>
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

internal sealed record CompressTo(CompressSpec Spec) : Operation
{
    public override string? NameSuffix => "compressed";
    public override string Describe() => "Compress";
}

/// <summary>Either a JPEG quality from 1 to 100, or the largest file size allowed, in bytes.</summary>
internal sealed record CompressSpec(int? Quality = null, long? MaxBytes = null);

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
