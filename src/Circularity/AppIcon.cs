using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace Circularity;

/// <summary>
/// Draws the app icon (an accent-coloured disc with swap arrows). The tray icon is drawn at runtime so
/// it follows the chosen accent colour; <see cref="WriteIco"/> produces Assets\Circularity.ico, the
/// fixed icon used for the exe, shortcuts and installer.
/// </summary>
internal static class AppIcon
{
    private const string SwapGlyph = "";

    /// <summary>Sizes Windows picks from for the taskbar, Start menu, Explorer views and installer.</summary>
    private static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static Icon Create(MediaColor accent, int size = 32)
    {
        using var bitmap = Draw(accent, size);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    public static System.Windows.Media.ImageSource ToImageSource(Icon icon) =>
        Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    /// <summary>Writes a multi-size .ico (PNG-compressed entries, supported since Windows Vista).</summary>
    public static void WriteIco(string path, MediaColor accent)
    {
        var images = IcoSizes.Select(size =>
        {
            using var bitmap = Draw(accent, size);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return (Size: size, Png: png.ToArray());
        }).ToList();

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        // ICONDIR: reserved, type 1 = icon, image count.
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);

        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            // ICONDIRENTRY: width/height (0 means 256), colours, reserved, planes, bit depth, size, offset.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images)
            writer.Write(png);
    }

    private static Bitmap Draw(MediaColor accent, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var fill = new SolidBrush(Color.FromArgb(accent.R, accent.G, accent.B));
        g.FillEllipse(fill, 1, 1, size - 2, size - 2);

        using var font = new Font(IconFontName(), size * 0.5f, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(SwapGlyph, font, Brushes.White, new RectangleF(0, size / 32f, size, size), format);
        return bitmap;
    }

    /// <summary>Segoe Fluent Icons ships with Windows 11; Windows 10 has the older Segoe MDL2 Assets.</summary>
    private static string IconFontName()
    {
        using var installed = new InstalledFontCollection();
        return installed.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }
}
