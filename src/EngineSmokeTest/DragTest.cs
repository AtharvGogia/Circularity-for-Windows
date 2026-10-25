using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Circularity;

namespace EngineSmokeTest;

/// <summary>
/// Drives a real OLE drag-and-drop onto the wheel with synthetic mouse input, to check the wheel
/// accepts drops and picks the button under the cursor. It moves the real mouse for a few seconds.
///
/// A "catcher" window sits behind the wheel, so if the wheel misses, the test file lands there
/// instead of on the desktop or in another app.
/// </summary>
internal static class DragTest
{
    public static int Run()
    {
        int failures = 0;
        var thread = new Thread(() => failures = RunOnSta());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Console.WriteLine(failures == 0 ? "Drag test passed." : $"Drag test: {failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static int RunOnSta()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CircularityDrag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "drag.png");
        WritePng(file);

        var settings = new Settings();
        var wheel = new WheelWindow();
        wheel.ApplySettings(settings);
        WheelAction? chosen = null;
        wheel.ActionChosen += (_, action) => chosen = action;

        var work = SystemParameters.WorkArea;
        var source = new Window
        {
            Width = 160, Height = 100, Left = work.Left + 80, Top = work.Top + 200,
            WindowStyle = WindowStyle.ToolWindow, Topmost = true, Title = "drag source",
            Background = Brushes.SteelBlue, ShowInTaskbar = false,
        };
        var catcher = new Window
        {
            Width = 700, Height = 600, Left = work.Left + 300, Top = work.Top + 60,
            WindowStyle = WindowStyle.None, Topmost = true, AllowDrop = true, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)), Title = "catcher",
        };
        bool caught = false;
        catcher.DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        catcher.Drop += (_, e) => caught = true;

        catcher.Show();
        source.Show();

        var sourceCenter = ScreenCenter(source);
        var catcherCenter = ScreenCenter(catcher);
        double dpi = VisualTreeHelper.GetDpi(catcher).DpiScaleX;

        int failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-40} {detail}");
            if (!ok)
                failures++;
        }

        // Where buttons sit, from a wheel laid out the same way, relative to its centre.
        var layout = new WheelView();
        layout.ApplySettings(settings);
        layout.SetActions(Formats.ActionsFor(new[] { file }, (_, _) => true).Actions, "");
        Point Screen(int target)
        {
            var at = layout.PointFor(target)!.Value;
            return new Point(catcherCenter.X + (at.X - layout.Extent / 2) * dpi, catcherCenter.Y + (at.Y - layout.Extent / 2) * dpi);
        }
        int lastInArc = layout.ArcOrder[^1];
        string lastInArcLabel = layout.Actions[lastInArc].Label;

        // Button 0 is at the top of the ring. For a .png the first button is "JPG".
        var cases = new (string Name, Point[] Path, string? Expect)[]
        {
            ("drop on top button", new[] { Screen(0) }, "JPG"),
            ("drag off the wheel", new[] { new Point(catcherCenter.X + settings.WheelSize * dpi, catcherCenter.Y) }, null),
            // Rest on More so the arc opens, then out to the far (left) end of the arc.
            ("drop in the More arc", new[] { Screen(WheelView.MoreIndex), Screen(lastInArc) }, lastInArcLabel),
        };

        foreach (var (name, path, expect) in cases)
        {
            bool expectDrop = expect != null;
            chosen = null;
            caught = false;

            // Hold the button down over the source, then start the drag directly, rather than relying
            // on the source window receiving the synthetic click (anything briefly on top can eat it).
            MoveTo(sourceCenter);
            Wait(150);
            Mouse(MOUSEEVENTF_LEFTDOWN);
            Wait(150);

            // The rest of the input runs on another thread because DoDragDrop blocks this one in its own loop.
            var input = new Thread(() =>
            {
                Thread.Sleep(200);
                Glide(sourceCenter, catcherCenter, 25);   // into the wheel's centre
                Thread.Sleep(250);
                var from = catcherCenter;
                foreach (var stop in path)
                {
                    Glide(from, stop, 12);
                    Thread.Sleep(300);
                    from = stop;
                }
                Mouse(MOUSEEVENTF_LEFTUP);
            });
            input.Start();

            wheel.ShowAt((int)catcherCenter.X, (int)catcherCenter.Y);
            var data = new DataObject(DataFormats.FileDrop, new[] { file });
            var result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
            input.Join();
            Wait(600); // Let the wheel's drop / dismiss timers run.
            Console.WriteLine($"      (drag finished with effect {result})");

            if (expectDrop)
                Check(name, chosen?.Label == expect && !caught, $"chosen: {chosen?.Label ?? "nothing"}, fell through to catcher: {caught}");
            else
                Check(name, chosen == null && !wheel.IsVisible, $"chosen: {chosen?.Label ?? "nothing"}, wheel visible: {wheel.IsVisible}");
        }

        wheel.Close();
        source.Close();
        catcher.Close();
        Directory.Delete(dir, recursive: true);
        return failures;
    }

    private static Point ScreenCenter(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var r);
        return new Point((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
    }

    private static void WritePng(string path)
    {
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, new byte[4 * 4 * 4], 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Glide(Point from, Point to, int steps)
    {
        for (int i = 1; i <= steps; i++)
        {
            MoveTo(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
            Thread.Sleep(20);
        }
    }

    private static void Wait(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
            DoEvents();
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Thread.Sleep(5);
    }

    // --- Synthetic input ---

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public MOUSEINPUT mi; } // MOUSEINPUT is the largest union member.

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private static void MoveTo(Point p)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int nx = (int)Math.Round((p.X - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((p.Y - vy) * 65535.0 / (vh - 1));
        Send(new MOUSEINPUT { dx = nx, dy = ny, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK });
    }

    private static void Mouse(uint flags) => Send(new MOUSEINPUT { dwFlags = flags });

    private static void Send(MOUSEINPUT mi) =>
        SendInput(1, new[] { new INPUT { type = INPUT_MOUSE, mi = mi } }, Marshal.SizeOf<INPUT>());
}
