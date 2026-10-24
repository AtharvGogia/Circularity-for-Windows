using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace Circularity;

/// <summary>
/// Draws the app icon (a miniature of the wheel). The tray icon is drawn at runtime so
/// it follows the chosen accent colour; <see cref="WriteIco"/> produces Assets\Circularity.ico, the
/// fixed icon used for the exe, shortcuts and installer.
/// </summary>
internal static class AppIcon
{
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

    /// <summary>
    /// A miniature of the wheel: a disc in the accent colour with a ring of six faint segments, and
    /// one, at 2 o'clock, lit in solid white.
    /// </summary>
    private static Bitmap Draw(MediaColor accent, int size)
    {
        const int Segments = 6;
        const int LitSegment = 1; // Segment 0 is at 12 o'clock, so segment 1 sits at 2 o'clock.
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // The accent with a slightly lighter top, for a little depth.
        float c = size / 2f;
        var baseColour = Color.FromArgb(accent.R, accent.G, accent.B);
        var top = Color.FromArgb(Lighten(accent.R), Lighten(accent.G), Lighten(accent.B));
        using (var disc = new LinearGradientBrush(new RectangleF(0, 0, size, size), top, baseColour, 90f))
            g.FillEllipse(disc, 0.5f, 0.5f, size - 1f, size - 1f);

        float outer = size * 0.41f, inner = size * 0.22f;
        // Small icons get relatively wider gaps so the segments stay distinct.
        float gapDegrees = size <= 20 ? 14 : 9;
        float sweep = 360f / Segments - gapDegrees;
        var lit = Color.White;
        var unlit = Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF);

        for (int i = 0; i < Segments; i++)
        {
            // GDI+ angles run clockwise from 3 o'clock; segment 0 is centred at 12 o'clock.
            float start = -90 + i * 360f / Segments - sweep / 2;
            using var path = new GraphicsPath();
            path.AddArc(c - outer, c - outer, outer * 2, outer * 2, start, sweep);
            path.AddArc(c - inner, c - inner, inner * 2, inner * 2, start + sweep, -sweep);
            path.CloseFigure();
            using var brush = new SolidBrush(i == LitSegment ? lit : unlit);
            g.FillPath(brush, path);
        }
        return bitmap;
    }

    private static int Lighten(byte channel) => channel + (255 - channel) * 18 / 100;
}
