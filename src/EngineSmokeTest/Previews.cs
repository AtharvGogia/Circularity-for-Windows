using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Circularity;
using Circularity.Engine;

namespace EngineSmokeTest;

/// <summary>Renders the wheel and the settings window to PNGs, to check the look without a mouse.</summary>
internal static class Previews
{
    /// <summary>Release notes as written on GitHub (the shape of 0.4.1's), with a bit of everything Markdown can do.</summary>
    private const string SampleReleaseNotes = """
        ## Circularity 0.5.0

        ### Save as HEIC and AVIF
        - **AVIF** works on every PC and keeps transparency.
        - **HEIC** where Windows has Microsoft's *HEVC extension*.

        ### Fixes
        1. Converting iPhone photos on PCs ~~without~~ lacking the HEVC extension.
        2. Compressing a video to a size, such as `25 MB`.

        > Crash reports are kept in `%LOCALAPPDATA%\Circularity\crashes`.

        ### Install
        Download **CircularitySetup-0.5.0.exe** below, or see the [release page](https://github.com/AtharvGogia/Circularity-for-Windows/releases).
        """;

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

                // Windows 11-like desktop backdrop: default blue wallpaper
                var desktopBg = new Grid
                {
                    Width = 800,
                    Height = 500,
                };
                // Windows 11 default blue wallpaper (#004D8D) with subtle gradient
                desktopBg.Background = new LinearGradientBrush(
                    Color.FromRgb(0x00, 0x4D, 0x8D),
                    Color.FromRgb(0x00, 0x30, 0x60),
                    90);

                // A few desktop icons (top-left area) - simple text blocks
                var iconsPanel = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(20, 20, 0, 0),
                };
                foreach (var iconText in new[] { "📁  This PC", "📁  Documents", "🖼️  Photos", "📄  Recycle Bin" })
                {
                    var btn = new TextBlock
                    {
                        Text = iconText,
                        Foreground = new SolidColorBrush(Colors.White),
                        FontSize = 11,
                        FontFamily = new FontFamily("Segoe UI"),
                        Margin = new Thickness(0, 4, 0, 4),
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            Color = Colors.Black,
                            BlurRadius = 4,
                            ShadowDepth = 1,
                            Opacity = 0.6
                        }
                    };
                    iconsPanel.Children.Add(btn);
                }
                desktopBg.Children.Add(iconsPanel);

                // Position the wheel near the center-bottom (where a drag might end up)
                var wheelContainer = new Grid
                {
                    Width = view.Extent,
                    Height = view.Extent,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 80), // Above taskbar area
                    Children = { view },
                };
                desktopBg.Children.Add(wheelContainer);

                // Taskbar at bottom - simple border
                var taskbar = new Border
                {
                    Height = 48,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x10)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                };
                desktopBg.Children.Add(taskbar);

                // Hosted in a real (offscreen) window and given a moment, so the hover and open
                // animations have finished by the time the snapshot is taken.
                var host = new Window
                {
                    Content = desktopBg, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                    Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                };
                host.Show();
                Settle(400);
                Save(desktopBg, System.IO.Path.Combine(folder, name + ".png"));
                host.Close();
            }

            var sample = new[] { System.IO.Path.Combine(System.IO.Path.GetTempPath(), "holiday.png") };
            File.WriteAllBytes(sample[0], new byte[2_400_000]); // Only its name and size are shown.
            var gradient = new DrawingVisual();
            using (var dc = gradient.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Colors.SkyBlue, Colors.DarkOrange, 30), null, new System.Windows.Rect(0, 0, 900, 600));
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
            TutorialWindow Tutorial(AppTheme theme, int chapter)
            {
                var window = new TutorialWindow(new Settings { Theme = theme }, (_, _) => true);
                window.Chapters.SelectedIndex = chapter;
                return window;
            }

            var windows = new (string Name, Func<AppTheme, Window> Create)[]
            {
                ("settings", theme => new SettingsWindow(new Settings { Theme = theme })),
                ("settings-buttons", theme => { var w = new SettingsWindow(WithPreset(theme)); return OnTab(w, w.ButtonsTab); }),
                ("settings-general", theme => { var w = new SettingsWindow(new Settings { Theme = theme }); return OnTab(w, (TabItem)w.Tabs.Items[2]); }),
                ("settings-about", theme => { var w = new SettingsWindow(new Settings { Theme = theme }, null, new Circularity.Engine.FfmpegConverter()); return OnTab(w, w.AboutTab); }),
                ("settings-features", theme => { var w = new SettingsWindow(new Settings { Theme = theme, OutputFolder = @"D:\Converted", CopyToClipboardOnFinish = true }); return OnTab(w, (TabItem)w.Tabs.Items[3]); }),
                ("settings-help", theme => { var w = new SettingsWindow(new Settings { Theme = theme }); return OnTab(w, (TabItem)w.Tabs.Items[4]); }),
                ("welcome", theme => new WelcomeWindow(new Settings { Theme = theme })),
                ("update", _ => { var w = new UpdateWindow(checkNow: false); w.ShowAvailable("0.5.0", SampleReleaseNotes); return w; }),
                ("tutorial", theme => Tutorial(theme, 0)),
                ("tutorial-dragging", theme => Tutorial(theme, 1)),
                ("tutorial-try", theme => Tutorial(theme, 3)),
                ("tutorial-click-mode", theme => Tutorial(theme, 4)),
                ("tutorial-after", theme => Tutorial(theme, 10)),
                ("resize", _ => new ResizeDialog(sample, (4032, 3024))),
                ("compress", _ => new CompressDialog(sample)),
                ("compress-video", _ => new VideoCompressDialog(sample, new MediaInfo(TimeSpan.FromSeconds(151), 1920, 1080, 30, 12_000_000, true))),
                ("compress-pdf", _ => new PdfCompressDialog(sample)),
                ("trim", _ => new TrimDialog("holiday.mp4", TimeSpan.FromSeconds(151.4), null,
                    _ => System.Threading.Tasks.Task.FromResult<System.Windows.Media.ImageSource?>(picture))),
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
                    if (name == "trim" || name.StartsWith("tutorial") || name == "welcome")
                        Settle(600); // Cut-point previews load, and wheels finish their opening animation.
                    // The window's own backdrop (Mica) isn't part of its content, so paint a plain one behind it.
                    var backdrop = theme == AppTheme.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
                    Save((FrameworkElement)window.Content, System.IO.Path.Combine(folder, $"{name}-{theme.ToString().ToLowerInvariant()}.png"),
                        new SolidColorBrush(backdrop));
                    window.Close();
                }
            }
            File.Delete(sample[0]);

            // The app icon at every size it's used, both versions, on light and dark, in a few accent colours.
            var sheet = new StackPanel();
            foreach (var (back, darkDisc) in new[] { (0xFFF3F3F3u, true), (0xFFF3F3F3u, false), (0xFF202020u, false), (0xFF202020u, true) })
            {
                foreach (var accent in new[] { "#8B5CF6", "#3B82F6", "#22C55E" })
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Background = new SolidColorBrush(Color.FromArgb((byte)(back >> 24), (byte)(back >> 16), (byte)(back >> 8), (byte)back)) };
                    foreach (int size in new[] { 256, 64, 48, 40, 32, 24, 20, 16 })
                    {
                        var colour = (Color)ColorConverter.ConvertFromString(accent);
                        row.Children.Add(new Image { Source = AppIcon.Draw(colour, darkDisc, size), Width = size, Height = size, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true });
                    }
                    sheet.Children.Add(row);
                }
            }
            RenderOptions.SetBitmapScalingMode(sheet, BitmapScalingMode.NearestNeighbor);
            Save(sheet, System.IO.Path.Combine(folder, "app-icon.png"));

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