using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Circularity;

namespace EngineSmokeTest;

/// <summary>
/// Checks the no-drag ways in: the hand-off pipe between launches, the Ctrl+Alt+C registration, the
/// wheel's click mode (driven by key events sent to the window, not the real keyboard), and reading
/// the selection from a real File Explorer window. The wheel and an Explorer window flash up briefly.
/// Run with Circularity itself closed, since it owns the pipe and the shortcut.
/// </summary>
internal static class InteractionTest
{
    public static int Run()
    {
        int failures = 0;
        var thread = new Thread(() => failures = RunOnSta());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Console.WriteLine(failures == 0 ? "Interaction test passed." : $"Interaction test: {failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static int RunOnSta()
    {
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name,-46} {detail}");
            if (!ok)
                failures++;
        }

        string dir = Path.Combine(Path.GetTempPath(), "CircularityInteraction-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string picture = Path.Combine(dir, "photo.png");
        WritePng(picture);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not selected");

        try
        {
            // 1. Hand-off between launches.
            using (var channel = new InstanceChannel())
            {
                string[]? received = null;
                channel.Received += lines => received = lines;
                channel.StartListening();
                bool sent = InstanceChannel.TrySend(new[] { picture, InstanceChannel.OpenSettingsMessage });
                Pump(() => received != null, 3000);
                Check("second launch hands its files over", sent && received?.SequenceEqual(new[] { picture, InstanceChannel.OpenSettingsMessage }) == true,
                    received == null ? "nothing received" : $"{received.Length} line(s)");
            }

            // 2. The shortcut can be claimed and released.
            using (var hotkey = new GlobalHotkey())
            {
                bool registered = hotkey.Register(Native.MOD_CONTROL | Native.MOD_ALT, 0x43);
                hotkey.Unregister();
                Check("Ctrl+Alt+C can be registered", registered, registered ? "" : "taken by another app (or Circularity is running)");
            }

            // 3. Click mode, by keyboard.
            var wheel = new WheelWindow();
            wheel.ApplySettings(new Settings());
            WheelAction? chosen = null;
            wheel.ActionChosen += (_, action) => chosen = action;
            var screen = SystemParameters.WorkArea;
            int cx = (int)(screen.Left + screen.Width / 2), cy = (int)(screen.Top + screen.Height / 2);

            wheel.ShowForFiles(new[] { picture }, cx, cy);
            Pump(() => false, 300);
            Press(wheel, Key.Right);
            Press(wheel, Key.Enter);
            Pump(() => chosen != null, 1000);
            // A .png's first button is JPG (PNG itself isn't offered).
            Check("arrow + Enter picks the first button", chosen?.Label == "JPG" && !wheel.IsOpen, chosen?.Label ?? "nothing chosen");

            chosen = null;
            wheel.ShowForFiles(new[] { picture }, cx, cy);
            Pump(() => false, 300);
            Press(wheel, Key.Left);
            Press(wheel, Key.Escape);
            Pump(() => false, 300);
            Check("Esc cancels", chosen == null && !wheel.IsOpen, chosen?.Label ?? "nothing chosen");
            Pump(() => !wheel.IsVisible, 1500);
            Check("wheel fades out and closes", !wheel.IsVisible);

            // 4. "More": an image offers more buttons than the ring holds, so the extras move into
            // the arc behind a More button at the bottom.
            chosen = null;
            wheel.ShowForFiles(new[] { picture }, cx, cy);
            Pump(() => false, 300);
            var view = (WheelView)wheel.Content;
            Check("more buttons than the ring holds", view.Actions.Count > WheelView.MaxRingButtons, $"{view.Actions.Count} buttons");
            Check("the ring is capped at six", view.RingOrder.Count == WheelView.MaxRingButtons, $"{view.RingOrder.Count} on the ring");
            Check("the bottom button is More", view.RingOrder.Count > 3 && view.RingOrder[3] == WheelView.MoreIndex);

            // Buttons that open a dialog sit left of "More", on the ring or in the arc.
            double middle = view.Width / 2;
            var misplaced = view.Actions
                .Select((action, i) => (action, i))
                .Where(x => x.action.OpensDialog && !(view.PointFor(x.i)?.X < middle - 1))
                .Select(x => x.action.Label)
                .ToList();
            Check("dialog buttons are all left of More", misplaced.Count == 0, string.Join(", ", misplaced));

            // Arrow onto More (it is the fourth of the six): that shows the arc.
            for (int i = 0; i < 4; i++)
                Press(wheel, Key.Right);
            Pump(() => view.IsExpanded, 1000);
            int inArc = view.ArcOrder.Count;
            Check("More opens the arc", view.IsExpanded && view.HotIndex == WheelView.MoreIndex
                && inArc == view.Actions.Count - (WheelView.MaxRingButtons - 1), $"hot {view.HotIndex}, {inArc} in the arc");

            // Down goes into the arc, onto the button under More; Left moves left along it.
            Press(wheel, Key.Down);
            int under = view.HotIndex;
            Press(wheel, Key.Left);
            bool movedLeft = view.PointFor(view.HotIndex)?.X < view.PointFor(under)?.X;
            Check("arrow keys move along the arc", view.ArcOrder.Contains(under) && movedLeft,
                $"{Label(view, under)} then {Label(view, view.HotIndex)}");

            Press(wheel, Key.Escape);
            Pump(() => !view.IsExpanded, 1000);
            Check("Esc steps back out of the arc", !view.IsExpanded && wheel.IsOpen && view.HotIndex == WheelView.MoreIndex,
                $"hot {view.HotIndex}");

            // Enter on More goes back in, onto the button under it, which Enter then takes.
            Press(wheel, Key.Enter);
            Pump(() => view.IsExpanded, 1000);
            string expected = Label(view, view.HotIndex);
            Press(wheel, Key.Enter);
            Pump(() => chosen != null, 1000);
            Check("a button in the arc can be chosen", chosen?.Label == expected && view.ArcOrder.Contains(view.Actions.ToList().IndexOf(chosen!)) && !wheel.IsOpen,
                chosen?.Label ?? "nothing chosen");
            Pump(() => !wheel.IsVisible, 1500);

            // 5. Near the bottom of the screen the arc wouldn't fit below, so More moves to the top and
            // the arc opens upwards; Up then goes into it.
            chosen = null;
            var bottom = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            wheel.ShowForFiles(new[] { picture }, bottom.Left + bottom.Width / 2, bottom.Bottom - 20);
            Pump(() => false, 300);
            Check("near the bottom, More moves to the top", view.ArcAbove && view.RingOrder[0] == WheelView.MoreIndex,
                $"arc above: {view.ArcAbove}, top button: {view.RingOrder[0]}");
            Press(wheel, Key.Right); // the first ring button, which is now More
            Press(wheel, Key.Up);
            Check("Up goes into an arc above the wheel", view.IsExpanded && view.ArcOrder.Contains(view.HotIndex), $"hot {Label(view, view.HotIndex)}");
            Press(wheel, Key.Escape);
            Press(wheel, Key.Escape);
            Pump(() => !wheel.IsVisible, 1500);

            wheel.ShowForFiles(new[] { picture }, cx, cy);
            Pump(() => false, 200);
            Check("mid-screen, the arc opens below again", !view.ArcAbove);
            Press(wheel, Key.Escape);
            Pump(() => !wheel.IsVisible, 1500);
            wheel.Close();

            // 4. Reading the selection from a real Explorer window.
            var (files, error) = SelectionFromExplorer(dir, "photo.png");
            Check("Explorer selection is read", files?.SequenceEqual(new[] { picture }, StringComparer.OrdinalIgnoreCase) == true,
                error ?? string.Join(", ", files?.Select(Path.GetFileName) ?? Array.Empty<string>()));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Explorer may still hold the folder open for a moment.
            }
        }
        return failures;
    }

    /// <summary>Opens the folder in Explorer, selects one file through the shell, and reads it back.</summary>
    private static (string[]? Files, string? Error) SelectionFromExplorer(string dir, string fileName)
    {
        Process.Start("explorer.exe", $"\"{dir}\"");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        string url = new Uri(dir).AbsoluteUri;
        dynamic? window = null;
        dynamic? item = null;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && item == null)
        {
            Pump(() => false, 300);
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (string.Equals((string)w.LocationURL, url, StringComparison.OrdinalIgnoreCase))
                    {
                        window = w;
                        item = w.Document.Folder.ParseName(fileName);
                    }
                }
                catch (Exception)
                {
                    // Still opening.
                }
            }
        }
        if (window == null || item == null)
            return (null, "Explorer window didn't open");

        try
        {
            // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_FOCUSED
            window.Document.SelectItem(item, 1 | 4 | 16);
            Pump(() => false, 500);
            return (ExplorerSelection.ForWindow(new IntPtr(Convert.ToInt64(window.HWND))), null);
        }
        finally
        {
            window.Quit();
        }
    }

    private static string Label(WheelView view, int target) =>
        target >= 0 && target < view.Actions.Count ? view.Actions[target].Label : target.ToString();

    private static void Press(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
    }

    private static void Pump(Func<bool> done, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void WritePng(string path)
    {
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgr32, null, new byte[8 * 8 * 4], 32);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
