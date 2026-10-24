using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace GestureSpike;

/// <summary>
/// Draws the app icon at runtime (an accent-coloured disc with swap arrows), so the tray icon
/// follows the chosen accent colour. A real .ico file comes with packaging.
/// </summary>
internal static class AppIcon
{
    private const string SwapGlyph = "";

    public static Icon Create(MediaColor accent, int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            using var fill = new SolidBrush(Color.FromArgb(accent.R, accent.G, accent.B));
            g.FillEllipse(fill, 1, 1, size - 2, size - 2);

            using var font = new Font(IconFontName(), size * 0.5f, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(SwapGlyph, font, Brushes.White, new RectangleF(0, 1, size, size), format);
        }

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

    /// <summary>Segoe Fluent Icons ships with Windows 11; Windows 10 has the older Segoe MDL2 Assets.</summary>
    private static string IconFontName()
    {
        using var installed = new InstalledFontCollection();
        return installed.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }
}
