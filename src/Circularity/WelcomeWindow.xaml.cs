using System;
using System.Windows;
using System.Windows.Threading;

namespace Circularity;

/// <summary>Shown on first launch: how to open the wheel, with a small wheel that cycles through its buttons.</summary>
internal partial class WelcomeWindow : Window
{
    private static readonly WheelAction[] DemoActions =
    {
        new(WheelActionKind.Convert, "PNG", Glyphs.Photo, "png"),
        new(WheelActionKind.Convert, "WEBP", Glyphs.Photo, "webp"),
        new(WheelActionKind.Convert, "PDF", Glyphs.Pdf, "pdf"),
        new(WheelActionKind.Convert, "GIF", Glyphs.Gif, "gif"),
        new(WheelActionKind.Resize, "Resize", Glyphs.Resize),
        new(WheelActionKind.Crop, "Crop", Glyphs.Crop),
    };

    private readonly WheelView _demo = new();
    private readonly DispatcherTimer _cycle = new() { Interval = TimeSpan.FromMilliseconds(850) };

    /// <summary>Raised when "Open settings" is clicked.</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>Raised when "Full tutorial" is clicked.</summary>
    public event Action? TutorialRequested;

    public WelcomeWindow(Settings settings)
    {
        InitializeComponent();

        var small = settings.Clone();
        small.WheelSize = 240;
        _demo.ApplySettings(small);
        // The view leaves room around the wheel for the More arc, which this demo never opens. Taking
        // that room back out of its layout size centres the wheel itself in the box (the box clips).
        _demo.Margin = new Thickness(-(_demo.Extent - small.WheelSize) / 2);
        _demo.SetActions(DemoActions, "holiday.jpg", "3.4 MB");
        DemoHost.Content = _demo;

        // Light up each button in turn, like a cursor moving around the wheel.
        int next = 0;
        _cycle.Tick += (_, _) =>
        {
            _demo.SetHot(next);
            next = (next + 1) % DemoActions.Length;
        };
        Loaded += (_, _) =>
        {
            _cycle.Start();
            WindowHelpers.ForceForeground(this);
        };
        Closed += (_, _) => _cycle.Stop();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Close();
        OpenSettingsRequested?.Invoke();
    }

    private void Tutorial_Click(object sender, RoutedEventArgs e)
    {
        Close();
        TutorialRequested?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
