using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GestureSpike;

namespace EngineSmokeTest;

/// <summary>Renders the wheel and the settings window to PNGs, to check the look without a mouse.</summary>
internal static class Previews
{
    public static int Render(string folder)
    {
        int exitCode = 1;
        var thread = new Thread(() =>
        {
            Directory.CreateDirectory(folder);
            var actions = Formats.ActionsFor(new[] { "photo.jpg" }).Actions;

            var variants = new (string Name, Settings Settings)[]
            {
                ("wheel-default", new Settings()),
                ("wheel-small-blue-60pct", new Settings { WheelSize = 200, AccentColor = "#3B82F6", Opacity = 0.6 }),
                ("wheel-large-green", new Settings { WheelSize = 360, AccentColor = "#22C55E" }),
            };
            foreach (var (name, settings) in variants)
            {
                var view = new WheelView();
                view.ApplySettings(settings);
                view.SetActions(actions, "Image");
                view.SetHot(1);

                var backdrop = new Grid
                {
                    Width = view.Extent,
                    Height = view.Extent,
                    Background = new LinearGradientBrush(Color.FromRgb(0x5B, 0x7F, 0xD6), Color.FromRgb(0x2A, 0x9D, 0x8F), 45),
                    Children = { view },
                };
                Save(backdrop, Path.Combine(folder, name + ".png"));
            }

            WindowHelpers.SuppressForeground = true;
            var sample = new[] { Path.Combine(Path.GetTempPath(), "holiday.png") };
            File.WriteAllBytes(sample[0], new byte[2_400_000]); // Only its name and size are shown.
            var gradient = new DrawingVisual();
            using (var dc = gradient.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Colors.SkyBlue, Colors.DarkOrange, 30), null, new Rect(0, 0, 900, 600));
            var picture = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32);
            picture.Render(gradient);

            var windows = new (string Name, Window Window)[]
            {
                ("settings", new SettingsWindow(new Settings())),
                ("resize", new ResizeDialog(sample, (4032, 3024))),
                ("compress", new CompressDialog(sample)),
                ("crop", new CropWindow(picture, "holiday.png", 1, 2)),
            };
            foreach (var (name, window) in windows)
            {
                window.Left = -10000;
                window.Top = -10000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
                window.UpdateLayout();
                // The window's own backdrop (Mica) isn't part of its content, so paint a dark one behind it.
                Save((FrameworkElement)window.Content, Path.Combine(folder, name + ".png"),
                    new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
                window.Close();
            }
            File.Delete(sample[0]);

            exitCode = 0;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Console.WriteLine(exitCode == 0 ? $"Previews written to {folder}" : "Rendering failed.");
        return exitCode;
    }

    private static void Save(FrameworkElement element, string path, Brush? background = null)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();

        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (background != null)
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
