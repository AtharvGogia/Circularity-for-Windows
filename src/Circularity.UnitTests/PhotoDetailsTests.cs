using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using ImageConverter = Circularity.Engine.ImageConverter;
using Circularity.Engine;
using Xunit;

namespace Circularity.UnitTests;

/// <summary>Photos keep their details (when, with what, where) and colour profile, and turn upright.</summary>
public class PhotoDetailsTests : IDisposable
{
    private static readonly string Profile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\spool\drivers\color\sRGB Color Space Profile.icm");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"CircularityPhoto_{Guid.NewGuid():N}");

    public PhotoDetailsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("resize")]
    [InlineData("crop")]
    [InlineData("compress")]
    [InlineData("tiff")]
    public async Task Result_KeepsTheDetails_AndIsMarkedUpright(string what)
    {
        string photo = WritePhoto(64, 32, orientation: 6, withProfile: File.Exists(Profile));
        Operation operation = what switch
        {
            "resize" => new ResizeTo(new ResizeSpec(ResizeKind.Fit, 16, 16)),
            "crop" => new CropTo(new PixelRect(0, 0, 20, 30)),
            "compress" => new CompressTo(new CompressSpec(Quality: 70)),
            _ => new ConvertTo("tiff"),
        };

        string output = await Convert(photo, operation);

        var (frame, metadata) = Read(output);
        Assert.NotNull(metadata);
        Assert.Equal(new DateTime(2025, 9, 8, 10, 56, 5), DateTime.Parse(metadata!.DateTaken));
        Assert.Equal("Test Camera", metadata.CameraModel);
        string ifd = what == "tiff" ? "/ifd" : "/app1/ifd";
        Assert.Equal("N", metadata.GetQuery($"{ifd}/gps/{{ushort=1}}"));
        // The pixels are already turned, so viewers mustn't turn them again.
        Assert.Equal((ushort)1, metadata.GetQuery($"{ifd}/{{ushort=274}}"));
        // The size the camera wrote down is the result's.
        Assert.Equal((uint)frame.PixelWidth, System.Convert.ToUInt32(metadata.GetQuery($"{ifd}/exif/{{ushort=40962}}")));
        Assert.Equal((uint)frame.PixelHeight, System.Convert.ToUInt32(metadata.GetQuery($"{ifd}/exif/{{ushort=40963}}")));
        if (File.Exists(Profile))
            Assert.NotEmpty(frame.ColorContexts ?? new ReadOnlyCollection<ColorContext>(Array.Empty<ColorContext>()));
        if (what == "resize")
            Assert.Equal((8, 16), (frame.PixelWidth, frame.PixelHeight)); // Upright first (32 × 64), then fitted.
    }

    [Theory]
    // Left half red, right half blue, stored with each flag; where red ends up once upright.
    [InlineData(1, 16, 8, "left")]
    [InlineData(2, 16, 8, "right")]  // Mirrored.
    [InlineData(3, 16, 8, "right")]  // Half turn.
    [InlineData(4, 16, 8, "left")]   // Upside down.
    [InlineData(5, 8, 16, "top")]    // Mirrored along the diagonal.
    [InlineData(6, 8, 16, "top")]    // Quarter turn clockwise.
    [InlineData(7, 8, 16, "bottom")]
    [InlineData(8, 8, 16, "bottom")] // Quarter turn anticlockwise.
    public async Task EveryOrientation_ComesOutUpright(int orientation, int width, int height, string red)
    {
        string photo = WritePhoto(16, 8, (ushort)orientation, withProfile: false);

        string output = await Convert(photo, new ConvertTo("png"));

        var (frame, _) = Read(output);
        Assert.Equal((width, height), (frame.PixelWidth, frame.PixelHeight));
        var (x, y) = red switch
        {
            "left" => (1, height / 2),
            "right" => (width - 2, height / 2),
            "top" => (width / 2, 1),
            _ => (width / 2, height - 2),
        };
        var pixel = Pixel(frame, x, y);
        Assert.True(pixel.R > 180 && pixel.B < 90, $"Expected red at {red}, got {pixel}.");
        Assert.Equal((width, height), ImageConverter.ReadSize(photo));
    }

    [Fact]
    public async Task APhotoWithoutDetails_StillConverts()
    {
        string png = Path.Combine(_dir, "plain.png");
        Save(new PngBitmapEncoder(), Halves(8, 8), null, null, png);
        string output = await Convert(png, new ConvertTo("jpg"));
        Assert.Equal(8, Read(output).Frame.PixelWidth);
    }

    private async Task<string> Convert(string input, Operation operation)
    {
        var converter = new ImageConverter();
        string extension = converter.OutputExtension(input, operation);
        string output = Path.Combine(_dir, $"result {Guid.NewGuid():N}.{extension}");
        File.WriteAllBytes(output, Array.Empty<byte>());
        return await converter.RunAsync(input, operation, output, new Progress<double>(), CancellationToken.None);
    }

    /// <summary>A JPEG as a phone would write it: taken on 8 September 2025, with a camera, a location and a size.</summary>
    private string WritePhoto(int width, int height, ushort orientation, bool withProfile)
    {
        string path = Path.Combine(_dir, $"photo {Guid.NewGuid():N}.jpg");
        var metadata = new BitmapMetadata("jpg")
        {
            DateTaken = new DateTime(2025, 9, 8, 10, 56, 5).ToString(System.Globalization.CultureInfo.CurrentCulture),
            CameraModel = "Test Camera",
        };
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        metadata.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
        metadata.SetQuery("/app1/ifd/exif/{ushort=40962}", (uint)width);
        metadata.SetQuery("/app1/ifd/exif/{ushort=40963}", (uint)height);
        var profiles = withProfile ? new ReadOnlyCollection<ColorContext>(new[] { new ColorContext(new Uri(Profile)) }) : null;
        Save(new JpegBitmapEncoder { QualityLevel = 95 }, Halves(width, height), metadata, profiles, path);
        return path;
    }

    private static BitmapSource Halves(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool left = x < width / 2;
                pixels[i] = (byte)(left ? 0 : 255);     // Blue
                pixels[i + 2] = (byte)(left ? 255 : 0); // Red
                pixels[i + 3] = 255;
            }
        }
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static void Save(BitmapEncoder encoder, BitmapSource image, BitmapMetadata? metadata, ReadOnlyCollection<ColorContext>? profiles, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, profiles));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static (BitmapFrame Frame, BitmapMetadata? Metadata) Read(string path)
    {
        using var file = File.OpenRead(path);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return (frame, (frame.Metadata as BitmapMetadata)?.Clone());
    }

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
