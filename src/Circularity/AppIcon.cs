using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using MediaPen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Circularity;

/// <summary>
/// Draws the app icon: the wheel, with six segments around an accent hub and the one at 2 o'clock
/// lit in the accent colour, grown out to the disc's edge like a hovered button. The tray icon is
/// drawn at runtime so it follows the accent colour and the taskbar's light or dark mode;
/// <see cref="WriteIco"/> produces Assets\Circularity.ico, the fixed icon used for the exe,
/// shortcuts and installer.
/// </summary>
internal static class AppIcon
{
    /// <summary>Sizes Windows picks from for the taskbar, Start menu, Explorer views and installer.</summary>
    private static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    /// <summary>At this size and below, six segments blur together, so a simpler design is drawn.</summary>
    private const int TinyAt = 32;

    // The design, on a 512 × 512 canvas.
    private const double Canvas = 512, C = 256, Disc = 236;
    private const int LitSegment = 1; // Segment 0 is at 12 o'clock, so segment 1 sits at 2 o'clock.

    /// <param name="darkDisc">The charcoal version, for light backgrounds; otherwise the white one, for dark backgrounds.</param>
    /// <param name="size">Pixels; by default the size Windows uses for small icons (the tray, title bars).</param>
    public static Icon Create(MediaColor accent, bool darkDisc, int size = 0)
    {
        if (size <= 0)
            size = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
        using var bitmap = ToBitmap(Draw(accent, darkDisc, size));
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

    /// <summary>Writes a multi-size .ico (PNG-compressed entries, supported since Windows Vista), in the white version.</summary>
    public static void WriteIco(string path, MediaColor accent)
    {
        var images = IcoSizes.Select(size => (Size: size, Png: Png(Draw(accent, darkDisc: false, size)))).ToList();

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

    /// <summary>The icon as a picture, for previews.</summary>
    public static BitmapSource Draw(MediaColor accent, bool darkDisc, int size)
    {
        var palette = new Palette(accent, darkDisc);
        bool tiny = size <= TinyAt;
        double scale = size / Canvas;
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);

        // The shadow, blurred, under everything (only where it's big enough to see).
        if (!tiny)
        {
            var shadow = new DrawingVisual { Effect = new BlurEffect { Radius = 18 * scale, KernelType = KernelType.Gaussian } };
            using (var dc = shadow.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawEllipse(new SolidColorBrush(MediaColor.FromArgb((byte)(darkDisc ? 115 : 60), 0, 0, 0)), null, new Point(C, C + 8), Disc, Disc);
            }
            bitmap.Render(shadow);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawEllipse(palette.Disc, null, new Point(C, C), Disc, Disc);
            if (tiny)
                DrawTiny(dc, palette, darkDisc);
            else
                DrawFull(dc, palette);
        }
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Six segments with even gaps around the hub, the lit one grown out to the disc's edge.</summary>
    private static void DrawFull(DrawingContext dc, Palette palette)
    {
        const double hub = 58, gap = 18, round = 13, outer = 206;
        const double inner = hub + gap;
        for (int i = 0; i < 6; i++)
        {
            if (i != LitSegment)
                Rounded(dc, Segment(i * 60, inner, outer, gap, round), palette.Segment, round);
        }
        dc.PushClip(new EllipseGeometry(new Point(C, C), Disc, Disc));
        Rounded(dc, Segment(LitSegment * 60, inner, Disc + 40, gap, round), palette.Lit, round);
        dc.Pop();
        dc.DrawEllipse(palette.Hub, null, new Point(C, C), hub, hub);
        dc.DrawEllipse(null, new MediaPen(new SolidColorBrush(MediaColor.FromArgb(64, 255, 255, 255)), 2), new Point(C, C), hub - 1, hub - 1);
    }

    /// <summary>Few, bold shapes for small sizes: an edge, a solid ring, the lit segment cut out of it, and a big hub.</summary>
    private static void DrawTiny(DrawingContext dc, Palette palette, bool darkDisc)
    {
        const double hub = 84, gap = 40, round = 14, outer = 200;
        const double inner = hub + gap;
        var edge = darkDisc ? MediaColor.FromArgb(26, 255, 255, 255) : MediaColor.FromArgb(26, 0, 0, 0);
        dc.DrawEllipse(null, new MediaPen(new SolidColorBrush(edge), 16), new Point(C, C), Disc - 8, Disc - 8);
        dc.DrawEllipse(null, new MediaPen(palette.Segment, outer - inner), new Point(C, C), (inner + outer) / 2, (inner + outer) / 2);
        dc.PushClip(new EllipseGeometry(new Point(C, C), Disc, Disc));
        // A negative gap widens the cut past the segment's edges by half a gap on each side.
        dc.DrawGeometry(palette.Disc, null, Segment(LitSegment * 60, inner - 2, Disc + 40, -gap, 0));
        Rounded(dc, Segment(LitSegment * 60, inner, Disc + 40, gap, round), palette.Lit, round);
        dc.Pop();
        dc.DrawEllipse(palette.Hub, null, new Point(C, C), hub, hub);
    }

    /// <summary>
    /// Fills a shape drawn shrunk by the corner radius and strokes it with that radius and round
    /// joins, which rounds every corner exactly.
    /// </summary>
    private static void Rounded(DrawingContext dc, Geometry shape, MediaBrush brush, double round) =>
        dc.DrawGeometry(brush, round > 0 ? new MediaPen(brush, 2 * round) { LineJoin = PenLineJoin.Round } : null, shape);

    /// <summary>
    /// A ring segment centred on <paramref name="centre"/> degrees (clockwise from 12 o'clock), 60°
    /// wide, with parallel-sided gaps, already shrunk by <paramref name="round"/> for <see cref="Rounded"/>.
    /// </summary>
    private static Geometry Segment(double centre, double inner, double outer, double gap, double round)
    {
        double a1 = centre - 30, a2 = centre + 30, d = gap / 2 + round;
        double ri = inner + round, ro = outer - round;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(Corner(ro, a1, d, +1), true, true);
            g.ArcTo(Corner(ro, a2, d, -1), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, true);
            g.LineTo(Corner(ri, a2, d, -1), true, true);
            g.ArcTo(Corner(ri, a1, d, +1), new Size(ri, ri), 0, false, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Where a line parallel to the ray at <paramref name="angle"/>, <paramref name="d"/> away from it towards the segment, meets a circle.</summary>
    private static Point Corner(double radius, double angle, double d, int side)
    {
        double a = angle * Math.PI / 180, t = Math.Sqrt(radius * radius - d * d);
        double ux = Math.Sin(a), uy = -Math.Cos(a), nx = Math.Cos(a), ny = Math.Sin(a);
        return new Point(C + t * ux + side * d * nx, C + t * uy + side * d * ny);
    }

    /// <summary>The colours: gentle top-to-bottom gradients, with the segments a pale tint of the accent (or grey, on charcoal).</summary>
    private sealed class Palette
    {
        public MediaBrush Disc { get; }
        public MediaBrush Segment { get; }
        public MediaBrush Lit { get; }
        public MediaBrush Hub { get; }

        public Palette(MediaColor accent, bool dark)
        {
            Disc = Vertical(dark ? Rgb(0x3A3A3A) : Rgb(0xFFFFFF), dark ? Rgb(0x262626) : Rgb(0xF1F1F1));
            Segment = dark
                ? Vertical(Rgb(0x575757), Rgb(0x474747))
                : Vertical(Mix(accent, Colors.White, 0.73), Mix(accent, Colors.White, 0.55));
            var light = Mix(accent, Colors.White, 0.22);
            var deep = Mix(accent, Colors.Black, 0.10);
            Lit = Freeze(new LinearGradientBrush(light, deep, new Point(300, 40), new Point(470, 260)) { MappingMode = BrushMappingMode.Absolute });
            Hub = Freeze(new RadialGradientBrush(light, deep) { GradientOrigin = new Point(0.4, 0.35), Center = new Point(0.4, 0.35), RadiusX = 0.75, RadiusY = 0.75 });
        }

        // In canvas coordinates, so every shape shares one gradient (and a cut-out matches the disc under it).
        private static MediaBrush Vertical(MediaColor top, MediaColor bottom) =>
            Freeze(new LinearGradientBrush(top, bottom, new Point(0, 20), new Point(0, 492)) { MappingMode = BrushMappingMode.Absolute });

        private static MediaBrush Freeze(MediaBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        private static MediaColor Rgb(int rgb) => MediaColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static MediaColor Mix(MediaColor from, MediaColor to, double amount) => MediaColor.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static byte[] Png(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Bitmap ToBitmap(BitmapSource source)
    {
        // Copied out of the stream, which System.Drawing would otherwise need kept open.
        using var stream = new MemoryStream(Png(source));
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    }
}
