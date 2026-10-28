using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace Circularity.Engine;

/// <summary>
/// Image conversion, resizing, cropping and compression using the Windows Imaging Component (via WPF).
/// Reads anything WIC has a codec for, SVG (drawn with Svg.Skia), and anything else FFmpeg can read
/// (HEIC on PCs without the HEVC extension, AVIF without the AV1 one); writes PNG, JPG, BMP, GIF and TIFF itself,
/// HEIC with Windows' HEIF encoder where the PC has it, and WebP and AVIF by handing a PNG to FFmpeg
/// (libwebp, libaom) when it has those encoders.
/// </summary>
internal sealed class ImageConverter : IConverter
{
    private static readonly HashSet<string> Writable = new() { "png", "jpg", "bmp", "gif", "tiff" };

    /// <summary>WIC's HEIF container (GUID_ContainerFormatHeif), written with HEVC pictures inside: a .heic file.</summary>
    private static readonly Guid HeifContainer = new("e1e62521-6787-405b-a339-500715b5763f");

    /// <summary>
    /// Whether this PC can write HEIC. That takes Microsoft's HEIF and HEVC extensions (the HEVC one
    /// is preinstalled on many PCs, otherwise from the Store), so it's tried once rather than assumed.
    /// </summary>
    private static readonly Lazy<bool> HeifEncoderWorks = new(ProbeHeifEncoder);

    /// <summary>For tests: behave like a PC that can't write HEIC.</summary>
    internal static bool SkipHeicEncoder { get; set; }

    /// <summary>What decodes images Windows can't, for every caller of <see cref="LoadOriented"/>.</summary>
    private static FfmpegConverter? _fallbackDecoder;

    /// <summary>Whether the file is an SVG (handled by Svg.Skia).</summary>
    private static bool IsSvg(string path) =>
        Formats.NormalizedExtension(path) == "svg";

    /// <summary>For tests: behave like a PC with no Windows codec for any image, so FFmpeg decodes everything.</summary>
    internal static bool SkipWindowsDecoder { get; set; }

    private readonly FfmpegConverter? _ffmpeg;

    /// <param name="ffmpeg">Writes WebP (when it has libwebp), and reads images Windows has no codec for.</param>
    public ImageConverter(FfmpegConverter? ffmpeg = null)
    {
        _ffmpeg = ffmpeg;
        if (ffmpeg?.FfmpegPath != null)
            _fallbackDecoder = ffmpeg;
    }

    private bool CanWriteWebp => _ffmpeg?.CanEncodeWebp == true;
    private bool CanWriteAvif => _ffmpeg?.CanEncodeAvif == true;
    private static bool CanWriteHeic => !SkipHeicEncoder && HeifEncoderWorks.Value;

    /// <summary>Formats written with help (FFmpeg, or a Windows extension), when that help is there.</summary>
    private bool CanWrite(string format) => format switch
    {
        "webp" => CanWriteWebp,
        "avif" => CanWriteAvif,
        "heic" => CanWriteHeic,
        _ => Writable.Contains(format),
    };

    public bool CanHandle(string inputPath, Operation operation) =>
        Formats.KindOf(inputPath) == FileKind.Image && operation switch
        {
            ConvertTo convert => CanWrite(convert.Target),
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

        // Edits keep the format when it can be written: a resized HEIC stays HEIC where the PC can
        // write it, and becomes JPG where it can't. Icons and SVGs become PNG, since they're usually
        // see-through in places and JPG can't be.
        string ext = Formats.NormalizedExtension(inputPath);
        if (CanWrite(ext))
            return ext;
        return ext is "ico" or "svg" ? "png" : "jpg";
    }

    public async Task<string> RunAsync(string inputPath, Operation operation, string outputPath, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // WIC work is quick and can't be interrupted, so cancellation is only checked up front.
        cancellationToken.ThrowIfCancellationRequested();
        string format = OutputExtension(inputPath, operation);

        if (format is not ("webp" or "avif"))
        {
            await Sta.Run(() => Run(inputPath, operation, format, outputPath), "ImageConverter");
        }
        else
        {
            // WIC can't write WebP or AVIF: do the work into a lossless PNG, then have FFmpeg encode that.
            // Decoding stays with WIC so EXIF rotation and every readable format keep working.
            string png = Path.Combine(Path.GetTempPath(), $"Circularity-{Guid.NewGuid():N}.png");
            try
            {
                bool hasAlpha = await Sta.Run(() => Run(inputPath, operation, "png", png), "ImageConverter");
                if (format == "webp")
                    await _ffmpeg!.EncodeWebpAsync(png, outputPath, cancellationToken);
                else
                    await _ffmpeg!.EncodeAvifAsync(png, outputPath, hasAlpha, cancellationToken);
            }
            finally
            {
                File.Delete(png);
            }
        }

        progress.Report(1);
        return outputPath;
    }

    /// <summary>Does the work and saves it; returns whether the result has see-through pixels.</summary>
    private static bool Run(string inputPath, Operation operation, string format, string outputPath)
    {
        BitmapSource image = LoadOriented(inputPath);
        if (operation is CompressTo compress)
        {
            File.WriteAllBytes(outputPath, Compress(image, compress.Spec));
            return false;
        }

        image = operation switch
        {
            ResizeTo resize => Resize(image, resize.Spec.Apply(image.PixelWidth, image.PixelHeight)),
            CropTo crop => Crop(image, crop.Rect),
            _ => image,
        };
        Save(image, format, outputPath);
        return HasAlpha(image);
    }

    /// <summary>Saves a tiny HEIC in memory, on a thread of its own (WIC wants a single-threaded apartment).</summary>
    private static bool ProbeHeifEncoder()
    {
        bool works = false;
        var thread = new Thread(() =>
        {
            try
            {
                var encoder = BitmapEncoder.Create(HeifContainer);
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgr32, null, new byte[16 * 16 * 4], 16 * 4)));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                works = stream.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Write($"This PC can't write HEIC ({ex.Message}); it needs Microsoft's HEIF and HEVC extensions.");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return works;
    }

    /// <summary>
    /// Decodes the first frame, rotated upright according to its EXIF orientation. Frozen, so any
    /// thread can use it. When Windows has no codec for the file, FFmpeg decodes it instead.
    /// SVG files are decoded via Svg.Skia.
    /// </summary>
    public static BitmapSource LoadOriented(string path)
    {
        // SVG files are handled by Svg.Skia
        if (IsSvg(path))
            return LoadSvg(path);

        // A see-through AVIF: Windows would read only its colour, so FFmpeg puts its alpha picture back.
        bool mergeAlpha = Formats.NormalizedExtension(path) == "avif" && _fallbackDecoder?.HasAlphaPicture(path) == true;
        try
        {
            if (!SkipWindowsDecoder && !mergeAlpha)
                return LoadWithWindows(path);
        }
        catch (Exception ex) when (IsUnreadable(ex) && _fallbackDecoder != null)
        {
            Log.Write($"Windows couldn't decode {Path.GetFileName(path)} ({ex.Message.Trim()}); using FFmpeg.");
        }
        if (_fallbackDecoder == null)
            return LoadWithWindows(path);

        string png = Path.Combine(Path.GetTempPath(), $"Circularity-decoded-{Guid.NewGuid():N}.png");
        try
        {
            _fallbackDecoder.DecodeImageToPng(path, png, mergeAlpha);
            // FFmpeg has already turned it the right way up, and a PNG has no orientation tag to apply twice.
            return LoadWithWindows(png);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Couldn't read {Path.GetFileName(path)}. It may be damaged. ({ex.Message})", ex);
        }
        finally
        {
            File.Delete(png);
        }
    }

    private static BitmapSource LoadWithWindows(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        double angle = RotationFor(ReadOrientation(frame.Metadata as BitmapMetadata));
        BitmapSource image = angle == 0 ? frame : new TransformedBitmap(frame, new RotateTransform(angle));
        image.Freeze();
        return image;
    }

    /// <summary>What Windows throws for a file it has no codec for (or can't make sense of).</summary>
    private static bool IsUnreadable(Exception ex) =>
        ex is NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException or OverflowException;

    /// <summary>SVGs smaller than this (on their longest side) are drawn bigger; icons are often 16 or 24 px.</summary>
    private const int SvgSmall = 512;

    /// <summary>The size small SVGs are drawn at, and the most any SVG is drawn at.</summary>
    private const int SvgSmallTarget = 1024, SvgLargest = 4096;

    /// <summary>
    /// Draws an SVG (with Svg.Skia) into a frozen bitmap. Being vector, it can be drawn at any size, so
    /// a small one (an icon, say) comes out at 1024 px instead of a useless 24 px, and a huge one is
    /// capped at 4096 px. Transparent areas stay transparent.
    /// </summary>
    private static BitmapSource LoadSvg(string path)
    {
        using var svg = OpenSvg(path, out var bounds);
        var (width, height) = SvgOutputSize(bounds);
        float scale = width / bounds.Width;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"{Path.GetFileName(path)} is too large to draw.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(svg.Picture);

        // Straight from Skia's pixels into WPF's: the same premultiplied BGRA layout.
        var pixels = new byte[info.RowBytes * height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            if (!surface.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                throw new InvalidOperationException($"Couldn't draw {Path.GetFileName(path)}.");
        }
        finally
        {
            handle.Free();
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, info.RowBytes);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Parses an SVG; throws with a readable message if it isn't one Svg.Skia can draw.</summary>
    private static SKSvg OpenSvg(string path, out SKRect bounds)
    {
        var svg = new SKSvg();
        try
        {
            // Cleaned first, so drawing it never fetches anything from outside the file (see SvgSafety).
            using var stream = SvgSafety.Clean(path);
            svg.Load(stream);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            svg.Dispose();
            throw new InvalidOperationException($"Couldn't read {Path.GetFileName(path)}: it isn't a valid SVG.", ex);
        }
        if (svg.Picture is not { } picture)
        {
            svg.Dispose();
            throw new InvalidOperationException($"Couldn't read {Path.GetFileName(path)}: it isn't a valid SVG.");
        }
        bounds = picture.CullRect;
        // No size or viewBox at all: SVG's own default of 300 × 150.
        if (bounds.Width <= 0 || bounds.Height <= 0)
            bounds = new SKRect(0, 0, 300, 150);
        return svg;
    }

    /// <summary>The pixel size an SVG is drawn at (see <see cref="LoadSvg"/>).</summary>
    private static (int Width, int Height) SvgOutputSize(SKRect bounds)
    {
        float longest = Math.Max(bounds.Width, bounds.Height);
        float scale = longest < SvgSmall ? SvgSmallTarget / longest : Math.Min(1f, SvgLargest / longest);
        return (Math.Max(1, (int)Math.Round(bounds.Width * scale)), Math.Max(1, (int)Math.Round(bounds.Height * scale)));
    }

    /// <summary>Upright pixel size, read from the file header without decoding the whole image when Windows can.</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        // SVG files: read dimensions via Svg.Skia without full rasterization
        if (IsSvg(path))
            return ReadSvgSize(path);

        try
        {
            if (!SkipWindowsDecoder)
            {
                using var input = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                double angle = RotationFor(ReadOrientation(frame.Metadata as BitmapMetadata));
                return angle is 90 or 270 ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
            }
        }
        catch (Exception ex) when (IsUnreadable(ex) && _fallbackDecoder != null)
        {
            // Falls through to a full decode below.
        }
        var image = LoadOriented(path);
        return (image.PixelWidth, image.PixelHeight);
    }

    /// <summary>The size an SVG converts at, which is what the Resize window shows as its current size.</summary>
    private static (int Width, int Height) ReadSvgSize(string path)
    {
        using var svg = OpenSvg(path, out var bounds);
        return SvgOutputSize(bounds);
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
        // JPG and BMP have no transparency, and Windows' HEIF encoder drops it; put transparent pixels
        // on white instead of black.
        if (format is "jpg" or "bmp" or "heic" && HasAlpha(image))
            image = FlattenOnWhite(image);

        if (format == "heic")
        {
            try
            {
                HeifWriter.Save(image, outputPath);
                return;
            }
            catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidCastException)
            {
                // Through WPF instead: the same encoder at its own (higher) quality, so a bigger file, but a working one.
                Log.Write($"Couldn't set HEIC quality ({ex.Message}); saving at the encoder's default.");
            }
        }

        BitmapEncoder encoder = format switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" => new JpegBitmapEncoder { QualityLevel = 92 },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder(),
            "heic" => BitmapEncoder.Create(HeifContainer),
            _ => throw new NotSupportedException($"Can't write .{format} images."),
        };
        // The HEIF encoder takes plain 32-bit colour.
        if (format == "heic")
            image = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
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
