using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GestureSpike.Engine;

/// <summary>
/// Image conversion using the Windows Imaging Component (via WPF). Reads anything WIC has a
/// codec for (HEIC and WebP need the free Microsoft Store extensions); writes PNG, JPG, BMP,
/// GIF and TIFF.
/// </summary>
internal sealed class ImageConverter : IConverter
{
    public bool CanConvert(string inputPath, string target) =>
        Formats.KindOf(inputPath) == FileKind.Image && target is "png" or "jpg" or "bmp" or "gif" or "tiff";

    public Task ConvertAsync(string inputPath, string target, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // WIC encoding is quick and can't be interrupted, so cancellation is only checked up front.
        cancellationToken.ThrowIfCancellationRequested();

        // WPF imaging (RenderTargetBitmap in particular) needs an STA thread.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Convert(inputPath, target, outputPath);
                progress.Report(1);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ImageConverter",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void Convert(string inputPath, string target, string outputPath)
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
            _ => throw new NotSupportedException($"Can't write .{target} images."),
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var output = File.Create(outputPath);
        encoder.Save(output);
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
