using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Circularity;
using Circularity.Engine;

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
            var actions = Formats.ActionsFor(new[] { "photo.jpg" }, (_, _) => true).Actions;

            var variants = new (string Name, Settings Settings, bool Expanded)[]
            {
                ("wheel-default", new Settings(), false),
                ("wheel-more-open", new Settings(), true),
                ("wheel-more-open-light", new Settings { Theme = AppTheme.Light }, true),
                ("wheel-more-open-above", new Settings(), true),
                ("wheel-small-blue-60pct", new Settings { WheelSize = 200, AccentColor = "#3B82F6", Opacity = 0.6 }, false),
                ("wheel-large-green", new Settings { WheelSize = 360, AccentColor = "#22C55E" }, true),
                ("wheel-light", new Settings { Theme = AppTheme.Light }, false),
                ("wheel-light-blue-60pct", new Settings { Theme = AppTheme.Light, AccentColor = "#3B82F6", Opacity = 0.6 }, false),
            };
            WindowHelpers.SuppressForeground = true;
            foreach (var (name, settings, expanded) in variants)
            {
                var view = new WheelView();
                view.ApplySettings(settings);
                view.ArcAbove = name.EndsWith("-above");
                // A long name, to show it shortened in the middle.
                view.SetActions(actions, "IMG_20250908_105605_Pro.jpg", "3.4 MB");
                view.SetHot(expanded ? WheelView.MoreIndex : 1);
                if (expanded && view.ArcOrder.Count > 0)
                    view.SetHot(view.ArcOrder[^1]);

                var backdrop = new Grid
                {
                    Width = view.Extent,
                    Height = view.Extent,
                    Background = new LinearGradientBrush(Color.FromRgb(0x5B, 0x7F, 0xD6), Color.FromRgb(0x2A, 0x9D, 0x8F), 45),
                    Children = { view },
                };
                // Hosted in a real (offscreen) window and given a moment, so the hover and open
                // animations have finished by the time the snapshot is taken.
                var host = new Window
                {
                    Content = backdrop, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                    Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                };
                host.Show();
                Settle(400);
                Save(backdrop, Path.Combine(folder, name + ".png"));
                host.Close();
            }

            var sample = new[] { Path.Combine(Path.GetTempPath(), "holiday.png") };
            File.WriteAllBytes(sample[0], new byte[2_400_000]); // Only its name and size are shown.
            var gradient = new DrawingVisual();
            using (var dc = gradient.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Colors.SkyBlue, Colors.DarkOrange, 30), null, new Rect(0, 0, 900, 600));
            var picture = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32);
            picture.Render(gradient);

            // A saved button, so the Buttons tab shows one with its delete button.
            Settings WithPreset(AppTheme theme) => new()
            {
                Theme = theme,
                Presets = { new Preset("p1", "1920×1080", Resize: new ResizeSpec(ResizeKind.Fit, 1920, 1080)) },
            };
            SettingsWindow OnTab(SettingsWindow window, TabItem tab)
            {
                window.Tabs.SelectedItem = tab;
                return window;
            }

            var windows = new (string Name, Func<AppTheme, Window> Create)[]
            {
                ("settings", theme => new SettingsWindow(new Settings { Theme = theme })),
                ("settings-buttons", theme => { var w = new SettingsWindow(WithPreset(theme)); return OnTab(w, w.ButtonsTab); }),
                ("settings-general", theme => { var w = new SettingsWindow(new Settings { Theme = theme }); return OnTab(w, (TabItem)w.Tabs.Items[2]); }),
                ("settings-about", theme => { var w = new SettingsWindow(new Settings { Theme = theme }, null, new Circularity.Engine.FfmpegConverter()); return OnTab(w, w.AboutTab); }),
                ("welcome", theme => new WelcomeWindow(new Settings { Theme = theme })),
                ("resize", _ => new ResizeDialog(sample, (4032, 3024))),
                ("compress", _ => new CompressDialog(sample)),
                ("compress-video", _ => new VideoCompressDialog(sample, new MediaInfo(TimeSpan.FromSeconds(151), 1920, 1080, 30, 12_000_000, true))),
                ("compress-pdf", _ => new PdfCompressDialog(sample)),
                ("trim", _ => new TrimDialog("holiday.mp4", TimeSpan.FromSeconds(151.4), null,
                    _ => System.Threading.Tasks.Task.FromResult<ImageSource?>(picture))),
                ("crop", _ => new CropWindow(picture, "holiday.png", 1, 2)),
            };
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                foreach (var (name, create) in windows)
                {
                    var window = create(theme);
                    Theme.ApplyTo(window, theme);
                    window.Left = -10000;
                    window.Top = -10000;
                    window.ShowActivated = false;
                    window.ShowInTaskbar = false;
                    window.Show();
                    window.UpdateLayout();
                    // The About tab fills in once it has looked around the PC.
                    if (name == "settings-about")
                        Settle(2500);
                    if (name == "trim")
                        Settle(600); // The cut-point previews load after a short pause.
                    // The window's own backdrop (Mica) isn't part of its content, so paint a plain one behind it.
                    var backdrop = theme == AppTheme.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
                    Save((FrameworkElement)window.Content, Path.Combine(folder, $"{name}-{theme.ToString().ToLowerInvariant()}.png"),
                        new SolidColorBrush(backdrop));
                    window.Close();
                }
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

    /// <summary>Lets WPF's animation clock run for a while, pumping messages as a real app would.</summary>
    private static void Settle(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
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
