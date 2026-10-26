using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Circularity;
using Circularity.Engine;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;
using ShapePath = System.Windows.Shapes.Path;

namespace EngineSmokeTest;

/// <summary>
/// Renders the animated GIFs used in the README: short scenes built from the real wheel (with its real
/// animations) on a mock Windows 11 desktop (wallpaper, File Explorer, taskbar, notifications), with a
/// drawn cursor, captured frame by frame and encoded with FFmpeg. Nothing on the actual screen is recorded.
/// </summary>
internal static class ReadmeMedia
{
    private const int Width = 960, Height = 540, Fps = 25;
    private const double TaskbarHeight = 48;

    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");

    public static int Render(string folder)
    {
        string? ffmpeg = FfmpegConverter.Locate();
        if (ffmpeg == null)
        {
            Console.WriteLine("FFmpeg not found.");
            return 2;
        }
        Directory.CreateDirectory(folder);

        int exit = 0;
        var thread = new Thread(() =>
        {
            WindowHelpers.SuppressForeground = true;
            foreach (var (name, scene) in new (string, Func<Stage, double>)[] { ("drag", DragScene), ("keyboard", KeyboardScene), ("appearance", AppearanceScene) })
            {
                string frames = Path.Combine(Path.GetTempPath(), "circularity-frames-" + name);
                if (Directory.Exists(frames))
                    Directory.Delete(frames, true);
                Directory.CreateDirectory(frames);

                var stage = new Stage();
                stage.Record(scene, frames);
                stage.Close();

                string gif = Path.Combine(folder, name + ".gif");
                if (!Encode(ffmpeg, frames, gif))
                    exit = 1;
                Console.WriteLine($"{gif}: {new FileInfo(gif).Length / 1024} KB");
                Directory.Delete(frames, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static bool Encode(string ffmpeg, string frames, string gif)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-framerate", Fps.ToString(), "-i", Path.Combine(frames, "%04d.png"),
            "-vf", "split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
            "-loop", "0", gif,
        })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Console.WriteLine(error);
        return process.ExitCode == 0;
    }

    // --- Scenes. Each is called once per frame with the time in seconds; it returns the scene length. ---

    /// <summary>Drag a photo out of File Explorer, tap right-click, open More, drop it on GIF.</summary>
    private static double DragScene(Stage s)
    {
        double t = s.Time;
        var wheelAt = new Point(735, 235);
        const double DropAt = 5.0;
        if (s.Once("setup"))
        {
            s.Desktop(AppTheme.Dark);
            s.Explorer("Pictures", new[] { "holiday.jpg", "notes.md", "clip.mp4", "report.pdf" });
            s.WheelFor("holiday.jpg");
        }

        var tile = s.TileCenter(0);
        var path = new (double At, Point P)[]
        {
            (0.0, new Point(520, 470)),
            (0.8, tile),                                                // onto the photo
            (1.0, tile),                                                // press...
            (2.0, wheelAt),                                             // ...and drag it out of the window
            (2.8, s.ButtonPoint(wheelAt, 1)),                           // sweep across the ring
            (3.6, s.TargetPoint(wheelAt, WheelView.MoreIndex)),         // rest on More: the arc opens
            (DropAt, s.TargetPoint(wheelAt, s.IndexOfTarget("gif"))),   // out into the arc, onto GIF
        };
        var cursor = Along(path, t);
        s.Cursor(cursor);

        if (t >= 0.8 && s.Once("press"))
            s.Select(0);
        if (t >= 1.05 && t < DropAt)
            s.DragGhost(cursor, "holiday.jpg");
        if (t >= 2.0 && s.Once("rightclick"))
        {
            s.Ripple(wheelAt);
            s.Keys("Right-click");
            s.ShowWheel(wheelAt);
        }
        if (t >= 2.1 && t < DropAt)
        {
            s.HoverAt(cursor);
            s.DropLabel(s.LabelFor(s.HotTarget));
        }
        if (t >= DropAt && s.Once("drop"))
        {
            s.HideDragGhost();
            s.HideWheel();
        }
        if (t >= DropAt + 0.4 && s.Once("saved"))
            s.AddFile("holiday.gif");
        if (t >= DropAt + 0.5 && s.Once("toast"))
            s.Toast("holiday.gif is ready", "Converted holiday.jpg to GIF. Select to show it in File Explorer.");
        return DropAt + 2.6;
    }

    /// <summary>Select a PDF, press Ctrl+Alt+C, pick TXT with the arrow keys and Enter.</summary>
    private static double KeyboardScene(Stage s)
    {
        double t = s.Time;
        if (s.Once("setup"))
        {
            s.Desktop(AppTheme.Dark);
            s.Explorer("Documents", new[] { "report.pdf", "notes.md", "holiday.jpg", "clip.mp4" });
            s.WheelFor("report.pdf");
        }
        var tile = s.TileCenter(0);
        var rest = tile + new Vector(140, 60);
        // The pointer goes away once the keyboard takes over, as it would cover the wheel's centre.
        if (t < 2.3)
            s.Cursor(Along(new (double, Point)[] { (0.0, new Point(600, 470)), (0.7, tile), (1.2, tile), (1.6, rest) }, t));
        else
            s.Cursor(new Point(-50, -50));
        if (t >= 0.75 && s.Once("select"))
            s.Select(0);
        if (t >= 1.8 && s.Once("keys"))
            s.Keys("Ctrl", "Alt", "C");
        if (t >= 2.3 && s.Once("open"))
            s.ShowWheel(rest);
        for (int i = 0; i < 3; i++)
        {
            if (t >= 3.0 + i * 0.55 && s.Once("arrow" + i))
            {
                s.Keys("→");
                s.HoverIndex(i);
            }
        }
        if (t >= 4.8 && s.Once("enter"))
        {
            s.Keys("Enter");
            s.HideWheel();
        }
        if (t >= 5.1 && s.Once("saved"))
            s.AddFile("report.txt");
        if (t >= 5.2 && s.Once("toast"))
            s.Toast("report.txt is ready", "Converted report.pdf to TXT. Select to show it in File Explorer.");
        return 7.4;
    }

    /// <summary>The wheel in different accent colours, with the desktop following light and dark mode.</summary>
    private static double AppearanceScene(Stage s)
    {
        double t = s.Time;
        var center = new Point(Width / 2.0, (Height - TaskbarHeight) / 2.0);
        var looks = new (string Accent, AppTheme Theme)[]
        {
            ("#FF8A1E", AppTheme.Dark), ("#3B82F6", AppTheme.Dark), ("#22C55E", AppTheme.Dark),
            ("#EC4899", AppTheme.Light), ("#8B5CF6", AppTheme.Light), ("#06B6D4", AppTheme.Light),
        };
        if (s.Once("setup"))
        {
            s.Desktop(AppTheme.Dark);
            s.WheelFor("photo.png");
            s.ShowWheel(center);
        }
        for (int i = 0; i < looks.Length; i++)
        {
            if (t >= i * 1.0 && s.Once("look" + i))
            {
                s.Restyle(looks[i].Accent, looks[i].Theme, center);
                s.HoverIndex(i % 6);
            }
        }
        return looks.Length * 1.0;
    }

    /// <summary>Walks a cursor through timed waypoints, easing between them.</summary>
    private static Point Along((double At, Point P)[] path, double t)
    {
        for (int i = 1; i < path.Length; i++)
        {
            if (t <= path[i].At)
                return Lerp(path[i - 1].P, path[i].P, Ease((t - path[i - 1].At) / (path[i].At - path[i - 1].At)));
        }
        return path[^1].P;
    }

    private static double Ease(double x) => x < 0 ? 0 : x > 1 ? 1 : x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    private static Point Lerp(Point a, Point b, double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);

    private static SolidColorBrush Brush(uint argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static TextBlock Text(string text, double size, uint color, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, FontFamily = Ui, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock Glyph(int code, double size, uint color) => new()
    {
        Text = char.ConvertFromUtf32(code), FontFamily = Icons, FontSize = size, Foreground = Brush(color),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>An offscreen window holding the scene, so WPF runs its animations; frames are captured in real time.</summary>
    private sealed class Stage
    {
        private readonly Window _window;
        private readonly Canvas _desktop = new() { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, ClipToBounds = true };
        private readonly Canvas _overlay = new() { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, IsHitTestVisible = false };
        private readonly HashSet<string> _done = new();
        private readonly List<(Border Tile, Point Center)> _tiles = new();
        private readonly WheelView _wheel = new();
        private readonly Canvas _cursor;
        private Settings _settings = new() { Theme = AppTheme.Dark };
        private Canvas? _ghost;
        private Border? _dropLabel;
        private TextBlock? _dropText;
        private Border? _toast;
        private Rectangle? _wallpaper, _wallpaperNext;
        private Border? _taskbar;
        private AppTheme _desktopTheme = AppTheme.Dark;
        private Canvas? _explorer;
        private Point _explorerAt, _filesAt;
        private TextBlock? _status;

        public double Time { get; private set; }

        public Stage()
        {
            // The Windows arrow: white with a dark edge and a soft shadow.
            var arrow = new ShapePath
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.6,12.6 L7.8,19.6 L10.6,18.4 L7.5,11.6 L13.5,11.6 Z"),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round,
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1.5, Opacity = 0.45 },
            };
            _cursor = new Canvas { Children = { arrow }, RenderTransform = new ScaleTransform(1.3, 1.3) };
            _wheel.ApplySettings(_settings);
            _wheel.Visibility = Visibility.Hidden;

            var root = new Grid { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, Children = { _desktop, _overlay } };
            _window = new Window
            {
                Content = root, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            _window.Show();
        }

        public bool Once(string key) => _done.Add(key);

        public void Record(Func<Stage, double> scene, string folder)
        {
            var clock = Stopwatch.StartNew();
            double length = double.MaxValue;
            for (int frame = 0; frame / (double)Fps <= length; frame++)
            {
                Time = frame / (double)Fps;
                length = scene(this);
                // Let real time catch up so WPF's own animations are where they should be in this frame.
                while (clock.Elapsed.TotalSeconds < Time)
                    Pump();
                Pump();
                Save(Path.Combine(folder, $"{frame:D4}.png"));
            }
        }

        public void Close() => _window.Close();

        // --- The desktop ---

        /// <summary>Wallpaper, taskbar, and the wheel and cursor layers, in light or dark mode.</summary>
        public void Desktop(AppTheme theme)
        {
            _desktopTheme = theme;
            _wallpaper = new Rectangle { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, Fill = Wallpaper(theme) };
            _desktop.Children.Add(_wallpaper);
            _wallpaperNext = new Rectangle { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, Opacity = 0 };
            _desktop.Children.Add(_wallpaperNext);

            _taskbar = Taskbar(theme);
            Place(_taskbar, 0, ReadmeMedia.Height - TaskbarHeight);
            Place(_wheel, 0, 0);
            _overlay.Children.Add(_cursor);
            Cursor(new Point(-50, -50));
        }

        /// <summary>A soft "bloom" like Windows 11's default wallpaper, drawn once into a picture.</summary>
        private static ImageBrush Wallpaper(AppTheme theme)
        {
            bool dark = theme == AppTheme.Dark;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(dark ? Color.FromRgb(0x06, 0x12, 0x2E) : Color.FromRgb(0xD9, 0xE6, 0xF8),
                    dark ? Color.FromRgb(0x0B, 0x24, 0x4F) : Color.FromRgb(0xF2, 0xF5, 0xFB), 60), null, new Rect(0, 0, ReadmeMedia.Width, ReadmeMedia.Height));
                void Bloom(double x, double y, double r, Color c)
                {
                    var brush = new RadialGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B));
                    dc.DrawEllipse(brush, null, new Point(x, y), r, r * 0.8);
                }
                if (dark)
                {
                    Bloom(560, 330, 420, Color.FromArgb(0xB0, 0x1E, 0x5B, 0xD6));
                    Bloom(700, 260, 260, Color.FromArgb(0x90, 0x3E, 0x8B, 0xFF));
                    Bloom(420, 400, 240, Color.FromArgb(0x80, 0x5B, 0x3E, 0xC8));
                }
                else
                {
                    Bloom(560, 330, 420, Color.FromArgb(0xC0, 0x8C, 0xB8, 0xFF));
                    Bloom(700, 260, 260, Color.FromArgb(0xA0, 0xB5, 0xD3, 0xFF));
                    Bloom(420, 400, 240, Color.FromArgb(0x90, 0xC8, 0xB6, 0xF5));
                }
            }
            var bitmap = new RenderTargetBitmap(ReadmeMedia.Width, ReadmeMedia.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return new ImageBrush(bitmap);
        }

        /// <summary>Windows 11's taskbar: centred apps, the tray (with Circularity in it) and the clock.</summary>
        private Border Taskbar(AppTheme theme)
        {
            bool dark = theme == AppTheme.Dark;
            uint fg = dark ? 0xFFFFFFFF : 0xFF1A1A1A;
            var bar = new Grid { Width = ReadmeMedia.Width, Height = TaskbarHeight };

            var apps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            apps.Children.Add(TaskbarButton(StartLogo(), false, dark));
            apps.Children.Add(new Border
            {
                Width = 150, Height = 32, Margin = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(16),
                Background = Brush(dark ? 0x22FFFFFFu : 0x0F000000u),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0),
                    Children = { Glyph(0xE721, 13, fg), new TextBlock { Text = "Search", FontFamily = Ui, FontSize = 12, Foreground = Brush(dark ? 0xB0FFFFFFu : 0xA0000000u), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } },
                },
            });
            apps.Children.Add(TaskbarButton(Glyph(0xE8B7, 18, 0xFFF4C042), true, dark));
            apps.Children.Add(TaskbarButton(Glyph(0xE774, 18, 0xFF3BA4F6), false, dark));
            apps.Children.Add(TaskbarButton(Glyph(0xE715, 17, 0xFF4A9CFF), false, dark));
            apps.Children.Add(TaskbarButton(Glyph(0xE713, 17, fg), false, dark));
            bar.Children.Add(apps);

            var tray = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
            tray.Children.Add(Spaced(Glyph(0xE70E, 11, fg), 10));
            tray.Children.Add(Spaced(new Image { Source = AppImage(), Width = 16, Height = 16 }, 12));
            tray.Children.Add(Spaced(Glyph(0xE701, 14, fg), 8));
            tray.Children.Add(Spaced(Glyph(0xE767, 14, fg), 8));
            tray.Children.Add(Spaced(Glyph(0xE83F, 14, fg), 12));
            tray.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "10:42 AM", FontFamily = Ui, FontSize = 11.5, Foreground = Brush(fg), HorizontalAlignment = HorizontalAlignment.Right },
                    new TextBlock { Text = "10/27/2026", FontFamily = Ui, FontSize = 11.5, Foreground = Brush(fg), HorizontalAlignment = HorizontalAlignment.Right },
                },
            });
            bar.Children.Add(tray);

            return new Border
            {
                Width = ReadmeMedia.Width, Height = TaskbarHeight, Child = bar,
                Background = Brush(dark ? 0xEC1C1C1Cu : 0xEEF3F3F3u),
                BorderBrush = Brush(dark ? 0x30FFFFFFu : 0x18000000u), BorderThickness = new Thickness(0, 1, 0, 0),
            };
        }

        private static FrameworkElement Spaced(FrameworkElement element, double right)
        {
            element.Margin = new Thickness(0, 0, right, 0);
            element.VerticalAlignment = VerticalAlignment.Center;
            return element;
        }

        private static Border TaskbarButton(FrameworkElement icon, bool running, bool dark)
        {
            var inner = new Grid { Children = { icon } };
            if (running)
            {
                inner.Children.Add(new Border
                {
                    Width = 16, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Brush(0xFF60CDFF),
                    VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -2),
                });
            }
            return new Border
            {
                Width = 40, Height = 40, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(4),
                Background = running ? Brush(dark ? 0x18FFFFFFu : 0x0C000000u) : Brushes.Transparent, Child = inner,
            };
        }

        /// <summary>The four-pane Windows start button, drawn rather than taken from a logo file.</summary>
        private static FrameworkElement StartLogo()
        {
            var grid = new Grid { Width = 18, Height = 18 };
            for (int i = 0; i < 4; i++)
            {
                var pane = new Border
                {
                    Width = 8.4, Height = 8.4, CornerRadius = new CornerRadius(1),
                    Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0xB3, 0xFF), Color.FromRgb(0x0F, 0x6C, 0xDB), 90),
                    HorizontalAlignment = i % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    VerticalAlignment = i < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                };
                grid.Children.Add(pane);
            }
            return grid;
        }

        private static ImageSource AppImage()
        {
            using var icon = AppIcon.Create(new Settings().Accent);
            return AppIcon.ToImageSource(icon);
        }

        // --- File Explorer ---

        /// <summary>A Windows 11 File Explorer window (dark), open on a folder with these files as thumbnails.</summary>
        public void Explorer(string folder, string[] files)
        {
            const double X = 36, Y = 30, W = 560, H = 420;
            const double TabRow = 40, AddressRow = 44, CommandRow = 44, StatusRow = 26, NavWidth = 150;
            var window = new Canvas { Width = W, Height = H, ClipToBounds = true };

            // Tabs, with the window's buttons on the right.
            var tabs = new Border { Width = W, Height = TabRow, Background = Brush(0xFF1C1C1C) };
            var tabRow = new Grid();
            tabRow.Children.Add(new Border
            {
                Width = 200, Height = 32, Margin = new Thickness(8, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(8, 8, 0, 0), Background = Brush(0xFF2B2B2B),
                Child = new DockPanel
                {
                    Margin = new Thickness(12, 0, 10, 0),
                    Children =
                    {
                        Docked(Glyph(0xE8BB, 9, 0xB0FFFFFF), Dock.Right),
                        Docked(Glyph(0xE8B7, 14, 0xFFF4C042), Dock.Left),
                        WithMargin(Text(folder, 12, 0xFFFFFFFF), new Thickness(8, 0, 0, 0)),
                    },
                },
            });
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (int code in new[] { 0xE921, 0xE922, 0xE8BB })
                controls.Children.Add(new Border { Width = 46, Height = TabRow, Child = Glyph(code, 10, 0xFFFFFFFF) });
            tabRow.Children.Add(controls);
            tabs.Child = tabRow;
            Add(window, tabs, 0, 0);

            // Back/forward/up/refresh, the path and the search box.
            var address = new DockPanel { Width = W, Height = AddressRow, Background = Brush(0xFF2B2B2B), LastChildFill = true };
            var nav = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0) };
            foreach (int code in new[] { 0xE72B, 0xE72A, 0xE74A, 0xE72C })
                nav.Children.Add(new Border { Width = 32, Child = Glyph(code, 13, code == 0xE72A ? 0x60FFFFFFu : 0xFFFFFFFFu) });
            address.Children.Add(Docked(nav, Dock.Left));
            address.Children.Add(Docked(new Border
            {
                Width = 150, Height = 30, Margin = new Thickness(6, 0, 10, 0), CornerRadius = new CornerRadius(4), Background = Brush(0xFF1F1F1F),
                BorderBrush = Brush(0x18FFFFFF), BorderThickness = new Thickness(1),
                Child = new DockPanel { Margin = new Thickness(10, 0, 8, 0), Children = { Docked(Glyph(0xE721, 12, 0xB0FFFFFF), Dock.Right), Text($"Search {folder}", 12, 0x90FFFFFF) } },
            }, Dock.Right));
            address.Children.Add(new Border
            {
                Height = 30, CornerRadius = new CornerRadius(4), Background = Brush(0xFF1F1F1F),
                BorderBrush = Brush(0x18FFFFFF), BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0),
                    Children =
                    {
                        Glyph(0xE7F8, 13, 0xFFFFFFFF), WithMargin(Glyph(0xE76C, 9, 0xA0FFFFFF), new Thickness(8, 0, 6, 0)),
                        Text("This PC", 12, 0xFFFFFFFF), WithMargin(Glyph(0xE76C, 9, 0xA0FFFFFF), new Thickness(6, 0, 6, 0)), Text(folder, 12, 0xFFFFFFFF),
                    },
                },
            });
            Add(window, address, 0, TabRow);

            // The command bar.
            var commands = new StackPanel { Orientation = Orientation.Horizontal, Width = W, Height = CommandRow, Background = Brush(0xFF2B2B2B) };
            commands.Children.Add(new Border
            {
                Margin = new Thickness(12, 0, 8, 0),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { Glyph(0xE710, 13, 0xFF60CDFF), WithMargin(Text("New", 12, 0xFFFFFFFF), new Thickness(6, 0, 0, 0)) } },
            });
            commands.Children.Add(new Border { Width = 1, Height = 22, Background = Brush(0x30FFFFFF), Margin = new Thickness(6, 0, 10, 0) });
            foreach (int code in new[] { 0xE8C6, 0xE8C8, 0xE77F, 0xE8AC, 0xE72D, 0xE74D })
                commands.Children.Add(new Border { Width = 34, Child = Glyph(code, 14, 0xFFFFFFFF) });
            commands.Children.Add(new Border { Width = 1, Height = 22, Background = Brush(0x30FFFFFF), Margin = new Thickness(8, 0, 12, 0) });
            commands.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0), Children = { Glyph(0xE8CB, 13, 0xFFFFFFFF), WithMargin(Text("Sort", 12, 0xFFFFFFFF), new Thickness(6, 0, 0, 0)) } });
            commands.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Glyph(0xE8FD, 13, 0xFFFFFFFF), WithMargin(Text("View", 12, 0xFFFFFFFF), new Thickness(6, 0, 0, 0)) } });
            Add(window, commands, 0, TabRow + AddressRow);

            // The navigation pane.
            double bodyTop = TabRow + AddressRow + CommandRow, bodyHeight = H - bodyTop - StatusRow;
            var navPane = new StackPanel { Width = NavWidth, Height = bodyHeight, Background = Brush(0xFF202020) };
            var places = new (int Glyph, uint Color, string Name)[]
            {
                (0xE80F, 0xFFFFFFFF, "Home"), (0xE7F4, 0xFF5FB2F8, "Desktop"), (0xE896, 0xFF4FC07F, "Downloads"),
                (0xE8A5, 0xFF8FA8C8, "Documents"), (0xE91B, 0xFF5FB2F8, "Pictures"), (0xE8D6, 0xFFF07A6A, "Music"), (0xE714, 0xFFB98CF5, "Videos"),
            };
            navPane.Children.Add(new Border { Height = 8 });
            foreach (var place in places)
            {
                bool here = place.Name == folder;
                navPane.Children.Add(new Border
                {
                    Height = 30, Margin = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4),
                    Background = here ? Brush(0x22FFFFFF) : Brushes.Transparent,
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0),
                        Children = { Glyph(place.Glyph, 14, place.Color), WithMargin(Text(place.Name, 12, 0xFFFFFFFF), new Thickness(10, 0, 0, 0)) },
                    },
                });
            }
            Add(window, navPane, 0, bodyTop);

            // The files, as large thumbnails.
            Add(window, new Border { Width = W - NavWidth, Height = bodyHeight, Background = Brush(0xFF191919) }, NavWidth, bodyTop);
            _explorer = window;
            _explorerAt = new Point(X, Y);
            _filesAt = new Point(NavWidth, bodyTop);
            foreach (string file in files)
                AddTile(file);

            // Status bar.
            _status = WithMargin(Text($"{files.Length} items", 11.5, 0xB0FFFFFF), new Thickness(12, 0, 0, 0));
            Add(window, new Border { Width = W, Height = StatusRow, Background = Brush(0xFF1C1C1C), Child = _status }, 0, H - StatusRow);

            var frame = new Border
            {
                Width = W, Height = H, CornerRadius = new CornerRadius(8), ClipToBounds = true,
                BorderBrush = Brush(0x40FFFFFF), BorderThickness = new Thickness(1),
                Child = window,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.5 },
            };
            // Rounded window corners: clip the content to the frame's shape.
            window.Clip = new RectangleGeometry(new Rect(0, 0, W - 2, H - 2), 7, 7);
            // Put the window under the wheel.
            _desktop.Children.Insert(_desktop.Children.IndexOf(_wheel), frame);
            Canvas.SetLeft(frame, X);
            Canvas.SetTop(frame, Y);
        }

        private static T Docked<T>(T element, Dock dock) where T : UIElement
        {
            DockPanel.SetDock(element, dock);
            return element;
        }

        private static T WithMargin<T>(T element, Thickness margin) where T : FrameworkElement
        {
            element.Margin = margin;
            return element;
        }

        private static void Add(Canvas canvas, UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            canvas.Children.Add(element);
        }

        /// <summary>A file as Explorer shows it in "Large icons": a thumbnail and its name.</summary>
        private static Border Tile(string name) => new()
        {
            Width = 112, Height = 118, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0),
                Children =
                {
                    new Image { Source = Thumbnail(name), Width = 88, Height = 72, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = name, FontFamily = Ui, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) },
                },
            },
        };

        /// <summary>A believable thumbnail: a sunset photo, a video still, or a document page.</summary>
        private static ImageSource Thumbnail(string name)
        {
            const int W = 88, H = 72;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                switch (Formats.KindOf(name))
                {
                    case FileKind.Image:
                    {
                        var photo = new Rect(2, 8, 84, 58);
                        dc.PushClip(new RectangleGeometry(photo, 3, 3));
                        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3A, 0x7B, 0xD5), Color.FromRgb(0xFF, 0xA8, 0x5C), 90), null, photo);
                        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0x8A)), null, new Point(58, 36), 9, 9);
                        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x2D, 0x3E, 0x63)), null, Geometry.Parse("M2,66 L22,40 L36,52 L52,32 L86,60 L86,66 Z"));
                        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x1C, 0x29, 0x45)), null, Geometry.Parse("M2,66 L2,56 L18,50 L40,58 L62,50 L86,58 L86,66 Z"));
                        dc.Pop();
                        break;
                    }
                    case FileKind.Video:
                    {
                        var still = new Rect(2, 10, 84, 52);
                        dc.PushClip(new RectangleGeometry(still, 3, 3));
                        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x10, 0x8A, 0x7E), Color.FromRgb(0x14, 0x3D, 0x6E), 30), null, still);
                        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x80, 0x0B, 0x22, 0x33)), null, Geometry.Parse("M2,62 L30,40 L50,54 L86,34 L86,62 Z"));
                        dc.Pop();
                        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x10, 0x10)), null, new Point(44, 36), 12, 12);
                        dc.DrawGeometry(Brushes.White, null, Geometry.Parse("M40,29 L52,36 L40,43 Z"));
                        break;
                    }
                    default:
                    {
                        bool pdf = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
                        var page = new Rect(22, 2, 44, 60);
                        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), 1), page, 2, 2);
                        for (int line = 0; line < 6; line++)
                            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xC9, 0xCF, 0xD8)), null, new Rect(28, 12 + line * 7, line % 3 == 2 ? 20 : 32, 2.5));
                        var badge = pdf ? Color.FromRgb(0xD9, 0x30, 0x25) : Color.FromRgb(0x3B, 0x82, 0xF6);
                        dc.DrawRoundedRectangle(new SolidColorBrush(badge), null, new Rect(14, 44, 32, 14), 2, 2);
                        var label = new FormattedText(Path.GetExtension(name).TrimStart('.').ToUpperInvariant(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface(Ui, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 9, Brushes.White, 1.0);
                        dc.DrawText(label, new Point(30 - label.Width / 2, 45.5));
                        break;
                    }
                }
            }
            var bitmap = new RenderTargetBitmap(W * 2, H * 2, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Puts a file in the next spot of Explorer's grid (three across).</summary>
        private Border AddTile(string file)
        {
            int i = _tiles.Count;
            double tx = _filesAt.X + 18 + (i % 3) * 124, ty = _filesAt.Y + 14 + (i / 3) * 128;
            var tile = Tile(file);
            Add(_explorer!, tile, tx, ty);
            _tiles.Add((tile, new Point(_explorerAt.X + tx + 56, _explorerAt.Y + ty + 46)));
            return tile;
        }

        /// <summary>A converted file turning up in the open folder, as it does in Explorer, with the item count going up.</summary>
        public void AddFile(string file)
        {
            var tile = AddTile(file);
            tile.Opacity = 0;
            tile.RenderTransformOrigin = new Point(0.5, 0.5);
            tile.RenderTransform = new ScaleTransform(0.9, 0.9);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tile.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            var grow = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease };
            ((ScaleTransform)tile.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ((ScaleTransform)tile.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            if (_status != null)
                _status.Text = $"{_tiles.Count} items";
        }

        public Point TileCenter(int index) => _tiles[index].Center;

        /// <summary>Explorer's selection: a light blue fill and edge.</summary>
        public void Select(int index)
        {
            foreach (var (tile, _) in _tiles)
            {
                tile.Background = Brushes.Transparent;
                tile.BorderBrush = Brushes.Transparent;
            }
            _tiles[index].Tile.Background = Brush(0x3360CDFF);
            _tiles[index].Tile.BorderBrush = Brush(0x8060CDFF);
        }

        // --- The wheel ---

        public void WheelFor(string file)
        {
            var (_, actions) = Formats.ActionsFor(new[] { file }, (_, _) => true);
            // The demo files aren't real, so they get typical sizes for the wheel's centre.
            string size = Formats.KindOf(file) switch { FileKind.Image => "3.4 MB", FileKind.Video => "48 MB", _ => "1.2 MB" };
            _wheel.SetActions(actions, file, size);
            if (!_desktop.Children.Contains(_wheel))
                Place(_wheel, 0, 0);
        }

        public void ShowWheel(Point at)
        {
            Canvas.SetLeft(_wheel, at.X - _wheel.Extent / 2);
            Canvas.SetTop(_wheel, at.Y - _wheel.Extent / 2);
            _wheel.SetHot(-1);
            _wheel.Visibility = Visibility.Visible;
            _wheel.PlayIntro();
        }

        public void HideWheel() => _wheel.PlayOutro(() => _wheel.Visibility = Visibility.Hidden);

        /// <summary>Where a button sits on screen, given where the wheel is centred.</summary>
        public Point TargetPoint(Point wheelAt, int target)
        {
            var middle = new Point(_wheel.Extent / 2, _wheel.Extent / 2);
            return wheelAt + ((_wheel.PointFor(target) ?? middle) - middle);
        }

        /// <summary>Where the n-th showing button sits on screen.</summary>
        public Point ButtonPoint(Point wheelAt, int index)
        {
            var order = _wheel.IsExpanded ? _wheel.ArcOrder : _wheel.RingOrder;
            return order.Count == 0 ? wheelAt : TargetPoint(wheelAt, order[index % order.Count]);
        }

        public int IndexOfTarget(string target) => _wheel.IndexOfTarget(target);

        public int HotTarget => _wheel.HotIndex;

        public void HoverAt(Point p) =>
            _wheel.SetHot(_wheel.HitTest(new Point(p.X - Canvas.GetLeft(_wheel), p.Y - Canvas.GetTop(_wheel))));

        /// <summary>Lights the n-th button that is actually showing (the ring, or the opened arc).</summary>
        public void HoverIndex(int index)
        {
            var order = _wheel.IsExpanded ? _wheel.ArcOrder : _wheel.RingOrder;
            if (order.Count > 0)
                _wheel.SetHot(order[index % order.Count]);
        }

        /// <summary>
        /// A new accent and theme. When the theme changes, the wallpaper crossfades and the taskbar
        /// switches too, the way Windows does in light and dark mode.
        /// </summary>
        public void Restyle(string accent, AppTheme theme, Point center)
        {
            _settings = _settings.Clone();
            _settings.AccentColor = accent;
            _settings.Theme = theme;
            _wheel.ApplySettings(_settings);
            Canvas.SetLeft(_wheel, center.X - _wheel.Extent / 2);
            Canvas.SetTop(_wheel, center.Y - _wheel.Extent / 2);

            if (theme != _desktopTheme && _wallpaper != null && _wallpaperNext != null)
            {
                _desktopTheme = theme;
                _wallpaperNext.Fill = Wallpaper(theme);
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400));
                fade.Completed += (_, _) =>
                {
                    _wallpaper.Fill = _wallpaperNext.Fill;
                    _wallpaperNext.BeginAnimation(UIElement.OpacityProperty, null);
                    _wallpaperNext.Opacity = 0;
                };
                _wallpaperNext.BeginAnimation(UIElement.OpacityProperty, fade);
                int at = _desktop.Children.IndexOf(_taskbar);
                _desktop.Children.Remove(_taskbar);
                _taskbar = Taskbar(theme);
                _desktop.Children.Insert(at, _taskbar);
                Canvas.SetLeft(_taskbar, 0);
                Canvas.SetTop(_taskbar, ReadmeMedia.Height - TaskbarHeight);
            }
        }

        // --- Pointer, drag and keys ---

        public void Cursor(Point p)
        {
            Canvas.SetLeft(_cursor, p.X);
            Canvas.SetTop(_cursor, p.Y);
        }

        /// <summary>The see-through thumbnail Windows draws under the cursor while dragging a file.</summary>
        public void DragGhost(Point cursor, string file)
        {
            if (_ghost == null)
            {
                // Below and to the right of the pointer, so the button being dropped on stays in view.
                var thumb = new Border
                {
                    Width = 70, Height = 58, CornerRadius = new CornerRadius(4), Background = Brush(0x5560CDFF),
                    BorderBrush = Brush(0x9060CDFF), BorderThickness = new Thickness(1),
                    Child = new Image { Source = Thumbnail(file), Width = 60, Height = 49 },
                    Opacity = 0.7,
                };
                _dropText = new TextBlock { FontFamily = Ui, FontSize = 12, Foreground = Brush(0xFF1A1A1A), VerticalAlignment = VerticalAlignment.Center };
                _dropLabel = new Border
                {
                    Padding = new Thickness(6, 3, 8, 3), Background = Brush(0xFFF0F6FF), BorderBrush = Brush(0xFF6E9FD8), BorderThickness = new Thickness(1),
                    Child = _dropText, Visibility = Visibility.Hidden,
                };
                _ghost = new Canvas();
                _ghost.Children.Add(thumb);
                Canvas.SetLeft(thumb, 14);
                Canvas.SetTop(thumb, 22);
                _ghost.Children.Add(_dropLabel);
                Canvas.SetLeft(_dropLabel, 24);
                Canvas.SetTop(_dropLabel, 74);
                _overlay.Children.Insert(0, _ghost);
            }
            _ghost.Visibility = Visibility.Visible;
            Canvas.SetLeft(_ghost, cursor.X);
            Canvas.SetTop(_ghost, cursor.Y);
        }

        public void HideDragGhost()
        {
            if (_ghost != null)
                _ghost.Visibility = Visibility.Hidden;
        }

        /// <summary>The label under the dragged file ("+ Convert to GIF"), as Circularity sets it over each button.</summary>
        public void DropLabel(string? text)
        {
            if (_dropLabel == null || _dropText == null)
                return;
            _dropLabel.Visibility = text == null ? Visibility.Hidden : Visibility.Visible;
            if (text != null)
                _dropText.Inlines.Clear();
            if (text == null)
                return;
            int split = text.IndexOf(' ');
            _dropText.Inlines.Add(new System.Windows.Documents.Run("+  ") { Foreground = Brush(0xFF2563EB), FontWeight = FontWeights.Bold });
            _dropText.Inlines.Add(new System.Windows.Documents.Run(text));
        }

        /// <summary>What Windows would say under the file while over this target.</summary>
        public string? LabelFor(int target)
        {
            if (target == WheelView.CenterIndex)
                return "Cancel";
            if (target == WheelView.MoreIndex)
                return "More options";
            var actions = _wheel.Actions;
            if (target < 0 || target >= actions.Count)
                return null;
            var action = actions[target];
            return action.Kind == WheelActionKind.Convert ? $"Convert to {action.Label}" : action.OpensDialog ? $"{action.Label}…" : action.Label;
        }

        /// <summary>A soft expanding ring, showing a click.</summary>
        public void Ripple(Point at)
        {
            var ring = new Ellipse
            {
                Width = 44, Height = 44, Stroke = Brushes.White, StrokeThickness = 2.5,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.3, 0.3),
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 },
            };
            Canvas.SetLeft(ring, at.X - 22);
            Canvas.SetTop(ring, at.Y - 22);
            _overlay.Children.Add(ring);
            var grow = new DoubleAnimation(0.3, 1.7, TimeSpan.FromMilliseconds(550)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(550)));
        }

        /// <summary>Keycaps above the taskbar, fading out after a moment.</summary>
        public void Keys(params string[] keys)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (string key in keys)
            {
                row.Children.Add(new Border
                {
                    Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(8),
                    Background = Brush(0xEE2A2A30), BorderBrush = Brush(0x55FFFFFF), BorderThickness = new Thickness(1, 1, 1, 3),
                    Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.4 },
                    Child = new TextBlock { Text = key, FontFamily = Ui, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold },
                });
            }
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(row, (ReadmeMedia.Width - row.DesiredSize.Width) / 2);
            Canvas.SetTop(row, ReadmeMedia.Height - TaskbarHeight - 66);
            _overlay.Children.Add(row);
            row.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            row.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { BeginTime = TimeSpan.FromSeconds(0.75) });
        }

        /// <summary>A Windows 11 notification from Circularity, sliding in above the clock.</summary>
        public void Toast(string title, string body)
        {
            _toast = new Border
            {
                Width = 360, Padding = new Thickness(16, 12, 16, 16), CornerRadius = new CornerRadius(8),
                Background = Brush(0xFF262626), BorderBrush = Brush(0x30FFFFFF), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.45 },
                Child = new StackPanel
                {
                    Children =
                    {
                        new DockPanel
                        {
                            Children =
                            {
                                Docked(Glyph(0xE8BB, 10, 0xB0FFFFFF), Dock.Right),
                                Docked(new Image { Source = AppImage(), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }, Dock.Left),
                                Text("Circularity", 12, 0xC0FFFFFF),
                            },
                        },
                        WithMargin(Text(title, 14, 0xFFFFFFFF, FontWeights.SemiBold), new Thickness(0, 12, 0, 0)),
                        WithMargin(new TextBlock { Text = body, FontFamily = Ui, FontSize = 12.5, Foreground = Brush(0xC8FFFFFF), TextWrapping = TextWrapping.Wrap }, new Thickness(0, 4, 0, 0)),
                    },
                },
                RenderTransform = new TranslateTransform(380, 0),
            };
            _toast.Measure(new Size(360, double.PositiveInfinity));
            Canvas.SetLeft(_toast, ReadmeMedia.Width - 360 - 12);
            Canvas.SetTop(_toast, ReadmeMedia.Height - TaskbarHeight - 12 - _toast.DesiredSize.Height);
            _overlay.Children.Add(_toast);
            ((TranslateTransform)_toast.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void Place(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            _desktop.Children.Add(element);
        }

        private void Save(string path)
        {
            var root = (FrameworkElement)_window.Content;
            var bitmap = new RenderTargetBitmap(ReadmeMedia.Width, ReadmeMedia.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }

        private static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }
}
