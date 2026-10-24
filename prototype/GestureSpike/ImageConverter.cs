using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GestureSpike;

/// <summary>
/// Real image conversion using the Windows Imaging Component (via WPF), so the prototype
/// does something useful end to end. Reads anything WIC has a codec for (HEIC and WebP
/// need the free Microsoft Store extensions); writes PNG, JPG, BMP, GIF and TIFF.
/// Must run on an STA thread.
/// </summary>
internal static class ImageConverter
{
    public static bool CanWrite(string target) => target is "png" or "jpg" or "bmp" or "gif" or "tiff";

    /// <summary>Converts one file and returns the path of the new copy next to it.</summary>
    public static string Convert(string inputPath, string target)
    {
        BitmapSource image;
        using (var input = File.OpenRead(inputPath))
        {
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            image = ApplyExifOrientation(frame, frame.Metadata as BitmapMetadata);
        }

        // JPG and BMP have no transparency; put transparent pixels on white instead of black.
        if (target is "jpg" or "bmp" && HasAlpha(image))
            image = FlattenOnWhite(image);

        BitmapEncoder encoder = target switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder(),
            _ => throw new NotSupportedException($"Can't write .{target} yet."),
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        string outputPath = UniqueOutputPath(inputPath, target);
        using (var output = File.Create(outputPath))
            encoder.Save(output);
        return outputPath;
    }

    /// <summary>"photo.heic" -> "photo.png", or "photo (1).png" if that already exists.</summary>
    private static string UniqueOutputPath(string inputPath, string target)
    {
        string dir = Path.GetDirectoryName(inputPath)!;
        string name = Path.GetFileNameWithoutExtension(inputPath);
        string candidate = Path.Combine(dir, $"{name}.{target}");
        for (int i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{name} ({i}).{target}");
        return candidate;
    }

    /// <summary>Phone photos are often stored sideways with an EXIF flag saying how to rotate them.</summary>
    private static BitmapSource ApplyExifOrientation(BitmapSource image, BitmapMetadata? metadata)
    {
        ushort orientation = 1;
        try
        {
            if (metadata?.GetQuery("System.Photo.Orientation") is ushort value)
                orientation = value;
        }
        catch (NotSupportedException)
        {
            // Format has no queryable metadata.
        }

        double angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        return angle == 0 ? image : new TransformedBitmap(image, new RotateTransform(angle));
    }

    private static bool HasAlpha(BitmapSource image)
    {
        var format = image.Format;
        if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ||
            format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64 ||
            format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float)
        {
            return true;
        }
        return image.Palette?.Colors.Any(c => c.A < 255) ?? false;
    }

    private static BitmapSource FlattenOnWhite(BitmapSource image)
    {
        int w = image.PixelWidth, h = image.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(image, new Rect(0, 0, w, h));
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return new FormatConvertedBitmap(target, PixelFormats.Bgr24, null, 0);
    }
}
