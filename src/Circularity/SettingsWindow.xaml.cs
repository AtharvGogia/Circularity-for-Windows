using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Circularity;

/// <summary>Wheel size, opacity, accent colour and which gestures are on. Changes apply live.</summary>
internal partial class SettingsWindow : Window
{
    /// <summary>Raised on every change with a copy of the new settings.</summary>
    public event Action<Settings>? SettingsChanged;

    private static readonly string[] Palette =
    {
        "#FF8A1E", "#F43F5E", "#EC4899", "#8B5CF6", "#3B82F6", "#06B6D4", "#14B8A6", "#22C55E", "#EAB308", "#94A3B8",
    };

    private static readonly WheelAction[] PreviewActions =
    {
        new(WheelActionKind.Convert, "PNG", Glyphs.Photo, "png"),
        new(WheelActionKind.Convert, "JPG", Glyphs.Photo, "jpg"),
        new(WheelActionKind.Convert, "GIF", Glyphs.Gif, "gif"),
        new(WheelActionKind.Convert, "TIFF", Glyphs.Photo, "tiff"),
        new(WheelActionKind.Resize, "Resize", Glyphs.Resize),
        new(WheelActionKind.Crop, "Crop", Glyphs.Crop),
    };

    private readonly WheelView _preview = new();
    private Settings _settings;
    private bool _loading;

    public SettingsWindow(Settings current)
    {
        InitializeComponent();
        _settings = current.Clone();

        _preview.ApplySettings(_settings);
        _preview.SetActions(PreviewActions, "Image");
        _preview.SetHot(0);
        PreviewHost.Content = _preview;

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"Circularity {version?.Major}.{version?.Minor}";

        SizeSlider.ValueChanged += (_, _) => OnControlChanged();
        OpacitySlider.ValueChanged += (_, _) => OnControlChanged();
        RightClickCheck.Click += (_, _) => OnControlChanged();
        ShiftCheck.Click += (_, _) => OnControlChanged();
        HotkeyCheck.Click += (_, _) => OnControlChanged();
        NotifyFinishCheck.Click += (_, _) => OnControlChanged();
        NotifyStartCheck.Click += (_, _) => OnControlChanged();
        foreach (var option in new[] { SystemTheme, LightTheme, DarkTheme })
            option.Checked += (_, _) => OnControlChanged();

        // Lives in the registry (shared with the installer) rather than settings.json, so it's read
        // and written directly, and "Reset to defaults" leaves it alone.
        StartupCheck.IsChecked = StartAtSignIn.IsEnabled;
        StartupCheck.Click += (_, _) => OnStartupClicked();

        LoadControls();
    }

    private void LoadControls()
    {
        _loading = true;
        SizeSlider.Value = _settings.WheelSize;
        OpacitySlider.Value = Math.Round(_settings.Opacity * 100);
        RightClickCheck.IsChecked = _settings.RightClickTrigger;
        ShiftCheck.IsChecked = _settings.ShiftTrigger;
        HotkeyCheck.IsChecked = _settings.HotkeyEnabled;
        NotifyFinishCheck.IsChecked = _settings.NotifyOnFinish;
        NotifyStartCheck.IsChecked = _settings.NotifyOnStart;
        SystemTheme.IsChecked = _settings.Theme == AppTheme.System;
        LightTheme.IsChecked = _settings.Theme == AppTheme.Light;
        DarkTheme.IsChecked = _settings.Theme == AppTheme.Dark;
        _loading = false;
        BuildSwatches();
        Refresh();
    }

    private void OnControlChanged()
    {
        if (_loading)
            return;

        _settings.WheelSize = (int)SizeSlider.Value;
        _settings.Opacity = OpacitySlider.Value / 100;
        _settings.RightClickTrigger = RightClickCheck.IsChecked == true;
        _settings.ShiftTrigger = ShiftCheck.IsChecked == true;
        _settings.HotkeyEnabled = HotkeyCheck.IsChecked == true;
        _settings.NotifyOnFinish = NotifyFinishCheck.IsChecked == true;
        _settings.NotifyOnStart = NotifyStartCheck.IsChecked == true;
        _settings.Theme = LightTheme.IsChecked == true ? AppTheme.Light
            : DarkTheme.IsChecked == true ? AppTheme.Dark
            : AppTheme.System;
        Refresh();
        SettingsChanged?.Invoke(_settings.Clone());
    }

    private void Refresh()
    {
        SizeText.Text = $"{_settings.WheelSize} px";
        OpacityText.Text = $"{Math.Round(_settings.Opacity * 100)}%";
        _preview.ApplySettings(_settings);
        Theme.ApplyTo(this, _settings.Theme);
    }

    private void OnStartupClicked()
    {
        bool enable = StartupCheck.IsChecked == true;
        try
        {
            StartAtSignIn.Set(enable);
            Log.Write(enable ? "Will start at sign-in." : "Won't start at sign-in.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            Log.Write($"Couldn't change start-at-sign-in: {ex.Message}");
            StartupCheck.IsChecked = StartAtSignIn.IsEnabled;
            MessageBox.Show(this, $"Couldn't change this setting: {ex.Message}", "Circularity");
        }
    }

    private void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (string hex in Palette)
            Swatches.Children.Add(Swatch(hex));

        var custom = new Button { Content = "Custom…", Margin = new Thickness(4, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        custom.Click += (_, _) => PickCustomColor();
        Swatches.Children.Add(custom);
    }

    private FrameworkElement Swatch(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        bool selected = string.Equals(hex, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);

        // A ring around the selected colour; the gap between ring and dot keeps it visible on any theme.
        var ring = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(2),
            BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)) : Brushes.Transparent,
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 6, 8),
            Cursor = Cursors.Hand,
            ToolTip = hex,
            Child = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(color) },
        };
        ring.MouseLeftButtonUp += (_, _) => SetAccent(hex);
        return ring;
    }

    private void PickCustomColor()
    {
        var current = _settings.Accent;
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            SetAccent($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private void SetAccent(string hex)
    {
        _settings.AccentColor = hex;
        BuildSwatches();
        Refresh();
        SettingsChanged?.Invoke(_settings.Clone());
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Not a preference, so resetting shouldn't bring the welcome back.
        _settings = new Settings { WelcomeShown = _settings.WelcomeShown };
        LoadControls();
        SettingsChanged?.Invoke(_settings.Clone());
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
