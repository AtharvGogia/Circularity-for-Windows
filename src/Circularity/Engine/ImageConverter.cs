using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Circularity.Engine;

/// <summary>
/// Image conversion, resizing, cropping and compression using the Windows Imaging Component (via WPF).
/// Reads anything WIC has a codec for (HEIC needs the free Microsoft Store extension); writes PNG, JPG,
/// BMP, GIF and TIFF itself, and WebP by handing a PNG to FFmpeg (libwebp) when it has that encoder.
/// </summary>
internal sealed class ImageConverter : IConverter
{
    private static readonly HashSet<string> Writable = new() { "png", "jpg", "bmp", "gif", "tiff" };

    private readonly FfmpegConverter? _ffmpeg;

    /// <param name="ffmpeg">Used to write WebP; without it (or without libwebp), WebP isn't offered.</param>
    public ImageConverter(FfmpegConverter? ffmpeg = null)
    {
        _ffmpeg = ffmpeg;
    }

    private bool CanWriteWebp => _ffmpeg?.CanEncodeWebp == true;

    public bool CanHandle(string inputPath, Operation operation) =>
        Formats.KindOf(inputPath) == FileKind.Image && operation switch
        {
            ConvertTo { Target: "webp" } => CanWriteWebp,
            ConvertTo convert => Writable.Contains(convert.Target),
            ResizeTo or CropTo or CompressTo => true,
            _ => false,
        };

    public string OutputExtension(string inputPath, Operation operation)
    {
        if (operation is ConvertTo convert)
            return convert.Target;
        // Quality-based compression needs a lossy format; JPEG is the one WIC can write.
        if (operation is CompressTo)
            return "jpg";

        // Edits keep the format when it can be written. HEIC can only be read, so it becomes JPG.
        string ext = Formats.NormalizedExtension(inputPath);
        if (Writable.Contains(ext) || (ext == "webp" && CanWriteWebp))
            return ext;
        return ext == "ico" ? "png" : "jpg";
    }

    public async Task<string> RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // WIC work is quick and can't be interrupted, so cancellation is only checked up front.
        cancellationToken.ThrowIfCancellationRequested();
        string format = OutputExtension(inputPath, operation);

        if (format != "webp")
        {
            await Sta.Run(() => { Run(inputPath, operation, format, outputPath); return 0; }, "ImageConverter");
        }
        else
        {
            // WIC can't write WebP: do the work into a lossless PNG, then have FFmpeg encode that.
            // Decoding stays with WIC so EXIF rotation and every readable format keep working.
            string png = Path.Combine(Path.GetTempPath(), $"Circularity-{Guid.NewGuid():N}.png");
            try
            {
                await Sta.Run(() => { Run(inputPath, operation, "png", png); return 0; }, "ImageConverter");
                await _ffmpeg!.EncodeWebpAsync(png, outputPath, cancellationToken);
            }
            finally
            {
                File.Delete(png);
            }
        }

        progress.Report(1);
        return outputPath;
    }

    private void Run(string inputPath, Operation operation, string format, string outputPath)
    {
        BitmapSource image = LoadOriented(inputPath);
        if (operation is CompressTo compress)
        {
            File.WriteAllBytes(outputPath, Compress(image, compress.Spec));
            return;
        }

        image = operation switch
        {
            ResizeTo resize => Resize(image, resize.Spec.Apply(image.PixelWidth, image.PixelHeight)),
            CropTo crop => Crop(image, crop.Rect),
            _ => image,
        };
        Save(image, format, outputPath);
    }

    /// <summary>Decodes the first frame, rotated upright according to its EXIF orientation. Frozen, so any thread can use it.</summary>
    public static BitmapSource LoadOriented(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        double angle = RotationFor(ReadOrientation(frame.Metadata as BitmapMetadata));
        BitmapSource image = angle == 0 ? frame : new TransformedBitmap(frame, new RotateTransform(angle));
        image.Freeze();
        return image;
    }

    /// <summary>Upright pixel size, read from the file header without decoding the whole image.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        double angle = RotationFor(ReadOrientation(frame.Metadata as BitmapMetadata));
        return angle is 90 or 270 ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
    }

    private static BitmapSource Resize(BitmapSource image, (int Width, int Height) size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(image, new Rect(0, 0, size.Width, size.Height));

        var target = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    /// <summary>JPEG bytes at the requested quality, or the best quality that fits the size limit.</summary>
    private static byte[] Compress(BitmapSource image, CompressSpec spec)
    {
        // JPEG has no transparency, and the encoder wants plain 24-bit colour.
        image = HasAlpha(image) ? FlattenOnWhite(image) : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);

        if (spec.MaxBytes is not long maxBytes)
            return EncodeJpeg(image, Math.Clamp(spec.Quality ?? 80, 1, 100));

        for (int attempt = 0; attempt < 15; attempt++)
        {
            // Highest quality (between 10 and 95) that fits; size grows with quality, so binary search.
            byte[]? best = null;
            int low = 10, high = 95;
            while (low <= high)
            {
                int quality = (low + high) / 2;
                byte[] bytes = EncodeJpeg(image, quality);
                if (bytes.Length <= maxBytes)
                {
                    best = bytes;
                    low = quality + 1;
                }
                else
                {
                    high = quality - 1;
                }
            }
            if (best != null)
                return best;

            // Even low quality is too big, so shrink the picture by a fifth and try again.
            if (image.PixelWidth <= 32 || image.PixelHeight <= 32)
                break;
            var smaller = Resize(image, ((int)(image.PixelWidth * 0.8), (int)(image.PixelHeight * 0.8)));
            image = new FormatConvertedBitmap(smaller, PixelFormats.Bgr24, null, 0);
        }
        throw new InvalidOperationException($"Couldn't get the image under {FileSizes.Format(maxBytes)}.");
    }

    private static byte[] EncodeJpeg(BitmapSource image, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Crop(BitmapSource image, PixelRect rect)
    {
        int x = Math.Clamp(rect.X, 0, image.PixelWidth - 1);
        int y = Math.Clamp(rect.Y, 0, image.PixelHeight - 1);
        int w = Math.Clamp(rect.Width, 1, image.PixelWidth - x);
        int h = Math.Clamp(rect.Height, 1, image.PixelHeight - y);
        return new CroppedBitmap(image, new Int32Rect(x, y, w, h));
    }

    private static void Save(BitmapSource image, string format, string outputPath)
    {
        // JPG and BMP have no transparency; put transparent pixels on white instead of black.
        if (format is "jpg" or "bmp" && HasAlpha(image))
            image = FlattenOnWhite(image);

        BitmapEncoder encoder = format switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder(),
            _ => throw new NotSupportedException($"Can't write .{format} images."),
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var output = File.Create(outputPath);
        encoder.Save(output);
    }

    private static ushort ReadOrientation(BitmapMetadata? metadata)
    {
        try
        {
            if (metadata?.GetQuery("System.Photo.Orientation") is ushort value)
                return value;
        }
        catch (NotSupportedException)
        {
            // Format has no queryable metadata.
        }
        return 1;
    }

    /// <summary>Phone photos are often stored sideways with an EXIF flag saying how to rotate them.</summary>
    private static double RotationFor(ushort orientation) => orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };

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
