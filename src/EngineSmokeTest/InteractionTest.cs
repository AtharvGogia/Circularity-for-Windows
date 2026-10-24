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
