using System;
using System.Diagnostics;
using System.Threading;
using Circularity;
using Circularity.Engine;

namespace EngineSmokeTest;

/// <summary>
/// Times the wheel's work on each drag, to check performance changes:
///   dotnet run --project src/EngineSmokeTest -c Release -- --bench-wheel
/// </summary>
internal static class WheelBench
{
    private const int Runs = 40;

    public static int Run()
    {
        var thread = new Thread(() =>
        {
            WindowHelpers.SuppressForeground = true;
            var crowded = new Settings
            {
                ButtonLayouts = { ["Image"] = new ButtonLayout() },
                Presets =
                {
                    new Preset("p1", "Insta 1080", Resize: new ResizeSpec(ResizeKind.Fit, 1080, 1080)),
                    new Preset("p2", "Email", Compress: new CompressSpec(Quality: 60), Kind: "Image"),
                    new Preset("p3", "Thumb", Resize: new ResizeSpec(ResizeKind.Fit, 320, 320)),
                },
            };
            foreach (var (name, settings) in new[] { ("default", new Settings()), ("crowded", crowded) })
            {
                var files = new[] { "photo.jpg" };
                var actions = Formats.ActionsFor(files, (_, _) => true, settings).Actions;
                var other = Formats.ActionsFor(new[] { "clip.mp4" }, (_, _) => true, settings).Actions;
                var view = new WheelView();
                view.ApplySettings(settings);

                Time($"{name}: ActionsFor", () => Formats.ActionsFor(files, (_, _) => true, settings));
                Time($"{name}: ApplySettings", () => view.ApplySettings(settings.Clone()));
                int flip = 0;
                Time($"{name}: SetActions (new files)", () =>
                    view.SetActions(++flip % 2 == 0 ? actions : other, "IMG_1.jpg", "3.4 MB"));
                Time($"{name}: ArcAbove flip", () => view.ArcAbove = !view.ArcAbove);
                Time($"{name}: open More", () => { view.SetHot(-1); view.SetHot(WheelView.MoreIndex); });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return 0;
    }

    private static void Time(string what, Action action)
    {
        for (int i = 0; i < 5; i++)
            action(); // Warm up: JIT and first-use caches.
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Runs; i++)
            action();
        Console.WriteLine($"{what,-40} {watch.Elapsed.TotalMilliseconds / Runs,8:0.000} ms");
    }
}
