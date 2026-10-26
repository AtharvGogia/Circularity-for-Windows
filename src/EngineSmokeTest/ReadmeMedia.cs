using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Circularity;
using Circularity.Engine;

namespace EngineSmokeTest;

/// <summary>
/// Renders the animated GIFs used in the README: short scenes built from the real wheel (with its real
/// animations), a mock desktop and a drawn cursor, captured frame by frame and encoded with FFmpeg.
/// Nothing on the actual screen is recorded.
/// </summary>
internal static class ReadmeMedia
{
    private const int Width = 720, Height = 420, Fps = 25;

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
            "-vf", "split[a][b];[a]palettegen=max_colors=160:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
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

    private static readonly Point TileHome = new(150, 120);

    private static double DragScene(Stage s)
    {
        double t = s.Time;
        var wheelAt = new Point(380, 215);
        const double DropAt = 4.7;
        if (s.Once("setup"))
        {
            s.Desktop(new[] { "holiday.jpg", "notes.md", "clip.mp4" });
            s.WheelFor("holiday.jpg");
        }

        // Pick the file up, carry it to the wheel, sweep the ring, open "More", and land on GIF.
        var path = new (double At, Point P)[]
        {
            (0.0, new Point(560, 350)),
            (0.7, TileHome + new Vector(30, 30)),
            (1.7, wheelAt),
            (2.5, s.ButtonPoint(wheelAt, 1)),                           // sweep across the ring
            (3.3, s.TargetPoint(wheelAt, WheelView.MoreIndex)),         // pause on More: the arc opens
            (DropAt, s.TargetPoint(wheelAt, s.IndexOfTarget("gif"))),   // out into the arc, onto GIF
        };
        var cursor = Along(path, t);
        s.Cursor(cursor);
        if (t >= 0.7 && t < DropAt + 0.1)
            s.DragGhost(cursor - new Vector(30, 30), visible: true);

        if (t >= 1.7 && s.Once("rightclick"))
        {
            s.Ripple(wheelAt, "Right-click");
            s.ShowWheel(wheelAt);
        }
        if (t >= 1.8 && t < DropAt)
            s.HoverAt(cursor);

        if (t >= DropAt && s.Once("drop"))
        {
            s.DragGhost(default, visible: false);
            s.HideWheel();
        }
        if (t >= DropAt + 0.3 && s.Once("toast"))
            s.Toast("Done", "holiday.gif\nClick to show.");
        return DropAt + 2.1;
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

    private static double KeyboardScene(Stage s)
    {
        double t = s.Time;
        var cursorAt = new Point(380, 225);
        if (s.Once("setup"))
        {
            s.Desktop(new[] { "report.pdf", "holiday.jpg", "clip.mp4" });
            s.Select(0);
            s.WheelFor("report.pdf");
            s.Cursor(cursorAt);
        }
        if (t >= 0.5 && s.Once("keys"))
            s.Keys("Ctrl", "Alt", "C");
        if (t >= 1.1 && s.Once("open"))
            s.ShowWheel(cursorAt);
        for (int i = 0; i < 3; i++)
        {
            if (t >= 1.8 + i * 0.55 && s.Once("arrow" + i))
            {
                s.Keys("→");
                s.HoverIndex(i);
            }
        }
        if (t >= 3.6 && s.Once("enter"))
        {
            s.Keys("Enter");
            s.HideWheel();
        }
        if (t >= 3.9 && s.Once("toast"))
            s.Toast("Done", "report.txt\nClick to show.");
        return 5.6;
    }

    private static double AppearanceScene(Stage s)
    {
        double t = s.Time;
        var center = new Point(Width / 2.0, Height / 2.0);
        var looks = new (string Accent, AppTheme Theme)[]
        {
            ("#FF8A1E", AppTheme.Dark), ("#3B82F6", AppTheme.Dark), ("#22C55E", AppTheme.Dark),
            ("#EC4899", AppTheme.Light), ("#8B5CF6", AppTheme.Light), ("#06B6D4", AppTheme.Light),
        };
        if (s.Once("setup"))
        {
            s.Backdrop();
            s.WheelFor("photo.png");
            s.ShowWheel(center);
        }
        for (int i = 0; i < looks.Length; i++)
        {
            if (t >= i * 0.9 && s.Once("look" + i))
            {
                s.Restyle(looks[i].Accent, looks[i].Theme);
                s.HoverIndex(i % 6);
            }
        }
        return looks.Length * 0.9;
    }

    private static double Ease(double x) => x < 0 ? 0 : x > 1 ? 1 : 1 - Math.Pow(1 - x, 3);

    private static Point Lerp(Point a, Point b, double f) => new(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);

    /// <summary>An offscreen window holding the scene, so WPF runs its animations; frames are captured in real time.</summary>
    private sealed class Stage
    {
        private readonly Window _window;
        private readonly Canvas _canvas = new() { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, ClipToBounds = true };
        private readonly Canvas _overlay = new() { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, IsHitTestVisible = false };
        private readonly HashSet<string> _done = new();
        private readonly List<Border> _tiles = new();
        private readonly WheelView _wheel = new();
        private readonly ShapePath _cursor;
        private Settings _settings = new() { Theme = AppTheme.Dark };
        private Border? _ghost;
        private Point _wheelAt;
        private string[] _files = Array.Empty<string>();

        public double Time { get; private set; }

        public Stage()
        {
            _cursor = new ShapePath
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.6,12.6 L7.8,19.6 L10.6,18.4 L7.5,11.6 L13.5,11.6 Z"),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1.2,
                RenderTransform = new ScaleTransform(1.25, 1.25),
            };
            _wheel.ApplySettings(_settings);
            _wheel.Visibility = Visibility.Hidden;

            var root = new Grid { Width = ReadmeMedia.Width, Height = ReadmeMedia.Height, Children = { _canvas, _overlay } };
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

        public void Backdrop()
        {
            // Windows 11 default wallpaper blue with subtle gradient
            _canvas.Background = new LinearGradientBrush(
                Color.FromRgb(0x00, 0x4D, 0x8D),
                Color.FromRgb(0x00, 0x30, 0x60),
                90);
        }

        /// <summary>A mock desktop with a folder window, taskbar, and desktop icons.</summary>
        public void Desktop(string[] files)
        {
            Backdrop();
            _files = files;

            // Desktop icons (top-left)
            var iconTexts = new[] { "📁  This PC", "📁  Documents", "🖼️  Photos", "📄  Recycle Bin" };
            for (int i = 0; i < iconTexts.Length; i++)
            {
                var icon = new Border
                {
                    Width = 84, Height = 84, CornerRadius = new CornerRadius(8),
                    Background = Brushes.Transparent,
                    Child = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = iconTexts[i],
                                Foreground = Brushes.White,
                                FontSize = 11,
                                FontFamily = new FontFamily("Segoe UI"),
                                HorizontalAlignment = HorizontalAlignment.Center,
                                TextWrapping = TextWrapping.Wrap,
                                TextAlignment = TextAlignment.Center,
                                Effect = new System.Windows.Media.Effects.DropShadowEffect
                                {
                                    Color = Colors.Black,
                                    BlurRadius = 4,
                                    ShadowDepth = 1,
                                    Opacity = 0.7
                                }
                            },
                        },
                    },
                };
                Canvas.SetLeft(icon, 20 + (i % 2) * 94);
                Canvas.SetTop(icon, 20 + (i / 2) * 94);
                _canvas.Children.Add(icon);
            }

            // Taskbar at bottom
            var taskbar = new Border
            {
                Height = 48,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Width = Width,
            };
            var taskbarGrid = new Grid();
            // Start button
            var startBtn = new Border
            {
                Width = 48, Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock
                {
                    Text = "⊞", Foreground = Brushes.White, FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                }
            };
            taskbarGrid.Children.Add(startBtn);

            // Pinned apps
            var pinnedApps = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(60, 0, 0, 0),
            };
            foreach (var emoji in new[] { "📂", "🌐", "💬", "📧" })
            {
                pinnedApps.Children.Add(new Border
                {
                    Width = 40, Height = 32, Margin = new Thickness(4, 0, 4, 0),
                    Background = Brushes.Transparent, CornerRadius = new CornerRadius(6),
                    Child = new TextBlock { Text = emoji, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                });
            }
            taskbarGrid.Children.Add(pinnedApps);

            // System tray (right)
            var systemTray = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 16, 0),
            };
            foreach (var emoji in new[] { "🔊", "📶", "🔋" })
            {
                systemTray.Children.Add(new Border
                {
                    Width = 40, Height = 32, Margin = new Thickness(4, 0, 4, 0),
                    Background = Brushes.Transparent, CornerRadius = new CornerRadius(6),
                    Child = new TextBlock { Text = emoji, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                });
            }
            systemTray.Children.Add(new TextBlock
            {
                Text = DateTime.Now.ToString("HH:mm"),
                Foreground = Brushes.White, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            });
            taskbarGrid.Children.Add(systemTray);

            taskbar.Child = taskbarGrid;
            _canvas.Children.Add(taskbar);
            Canvas.SetLeft(taskbar, 0);
            Canvas.SetTop(taskbar, Height - 48);

            // File tiles (folder window)
            var window = new Border
            {
                Width = 300, Height = 210, CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x20, 0x20, 0x24)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = "Pictures", Foreground = Brushes.White, Opacity = 0.8, Margin = new Thickness(14, 10, 0, 0), FontSize = 13 },
            };
            Place(window, 40, 60);
            for (int i = 0; i < files.Length; i++)
            {
                var tile = Tile(files[i]);
                _tiles.Add(tile);
                Place(tile, TileHome.X - 20 + i * 92, TileHome.Y - 10);
            }
            Place(_wheel, 0, 0);
            _overlay.Children.Add(_cursor);
        }

        public void Select(int index)
        {
            _tiles[index].Background = new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6));
            _tiles[index].BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0x60, 0xA5, 0xFA));
        }

        public void WheelFor(string file)
        {
            var (_, actions) = Formats.ActionsFor(new[] { file }, (_, _) => true);
            // The demo files aren't real, so they get typical sizes for the wheel's centre.
            string size = Formats.KindOf(file) switch { FileKind.Image => "3.4 MB", FileKind.Video => "48 MB", _ => "1.2 MB" };
            _wheel.SetActions(actions, file, size);
            if (!_canvas.Children.Contains(_wheel))
                Place(_wheel, 0, 0);
        }

        public void ShowWheel(Point at)
        {
            _wheelAt = at;
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

        public void HoverAt(Point p) =>
            _wheel.SetHot(_wheel.HitTest(new Point(p.X - Canvas.GetLeft(_wheel), p.Y - Canvas.GetTop(_wheel))));

        /// <summary>Lights the n-th button that is actually showing (the ring, or the opened arc).</summary>
        public void HoverIndex(int index)
        {
            var order = _wheel.IsExpanded ? _wheel.ArcOrder : _wheel.RingOrder;
            if (order.Count > 0)
                _wheel.SetHot(order[index % order.Count]);
        }


        public void Restyle(string accent, AppTheme theme)
        {
            _settings = _settings.Clone();
            _settings.AccentColor = accent;
            _settings.Theme = theme;
            _wheel.ApplySettings(_settings);
            Canvas.SetLeft(_wheel, ReadmeMedia.Width / 2.0 - _wheel.Extent / 2);
            Canvas.SetTop(_wheel, ReadmeMedia.Height / 2.0 - _wheel.Extent / 2);
            // Keep the desktop wallpaper background (set by Backdrop); only update the wheel's theme.
        }

        public void Cursor(Point p)
        {
            Canvas.SetLeft(_cursor, p.X);
            Canvas.SetTop(_cursor, p.Y);
        }

        public void DragGhost(Point at, bool visible)
        {
            if (_ghost == null)
            {
                _ghost = Tile(_files.Length > 0 ? _files[0] : "file");
                _ghost.Opacity = 0.75;
                _overlay.Children.Insert(0, _ghost);
            }
            _ghost.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            Canvas.SetLeft(_ghost, at.X);
            Canvas.SetTop(_ghost, at.Y);
        }

        /// <summary>An expanding ring with a caption, showing a click or key press.</summary>
        public void Ripple(Point at, string caption)
        {
            var ring = new Ellipse { Width = 40, Height = 40, Stroke = Brushes.White, StrokeThickness = 2, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.3, 0.3) };
            Canvas.SetLeft(ring, at.X - 20);
            Canvas.SetTop(ring, at.Y - 20);
            _overlay.Children.Add(ring);
            var grow = new System.Windows.Media.Animation.DoubleAnimation(0.3, 1.6, TimeSpan.FromMilliseconds(500));
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500)));
            // Shown along the bottom like the keyboard scene's keycaps, so it never covers the wheel.
            Keys(caption);
        }

        /// <summary>Keycaps along the bottom, fading out after a moment.</summary>
        public void Keys(params string[] keys)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (string key in keys)
            {
                row.Children.Add(new Border
                {
                    Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(7),
                    Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x24, 0x24, 0x2A)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1, 1, 1, 3),
                    Child = new TextBlock { Text = key, Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold },
                });
            }
            row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(row, (ReadmeMedia.Width - row.DesiredSize.Width) / 2);
            Canvas.SetTop(row, ReadmeMedia.Height - 70);
            _overlay.Children.Add(row);
            FadeOutLater(row, 0.6);
        }

        public void Toast(string title, string body)
        {
            var toast = new Border
            {
                Width = 250, Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x2B, 0x2B, 0x31)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 14 },
                        new TextBlock { Text = body, Foreground = Brushes.White, Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0) },
                    },
                },
                RenderTransform = new TranslateTransform(40, 0),
                Opacity = 0,
            };
            Canvas.SetLeft(toast, ReadmeMedia.Width - 270);
            Canvas.SetTop(toast, ReadmeMedia.Height - 100);
            _overlay.Children.Add(toast);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            toast.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
            ((TranslateTransform)toast.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });
        }

        private static void FadeOutLater(UIElement element, double seconds) =>
            element.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250))
            {
                BeginTime = TimeSpan.FromSeconds(seconds),
            });

        private Border Tile(string name) => new()
        {
            Width = 84, Height = 84, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = Formats.KindOf(name) switch { FileKind.Image => Glyphs.Photo, FileKind.Video => Glyphs.Video, _ => Glyphs.Document },
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 30,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) },
                },
            },
        };

        private void Place(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            _canvas.Children.Add(element);
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
