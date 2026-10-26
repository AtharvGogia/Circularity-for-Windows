using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Raised when "Check for updates" is clicked.</summary>
    public event Action? CheckForUpdatesRequested;

    private static readonly string[] Palette =
    {
        "#FF8A1E", "#F43F5E", "#EC4899", "#8B5CF6", "#3B82F6", "#06B6D4", "#14B8A6", "#22C55E", "#EAB308", "#94A3B8",
    };

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly WheelView _preview = new();
    private readonly Func<string, Engine.Operation, bool> _canHandle;
    private readonly Engine.FfmpegConverter? _ffmpeg;
    private Settings _settings;
    private bool _loading;
    private string _sample = "photo.jpg";
    private FileKind _kind = FileKind.Image;
    private List<DiagnosticItem>? _diagnostics;

    /// <param name="canHandle">What this PC can convert, so the preview shows the same buttons as the real wheel.</param>
    /// <param name="ffmpeg">The FFmpeg in use, for the About tab.</param>
    public SettingsWindow(Settings current, Func<string, Engine.Operation, bool>? canHandle = null, Engine.FfmpegConverter? ffmpeg = null)
    {
        InitializeComponent();
        _settings = current.Clone();
        _canHandle = canHandle ?? ((_, _) => true);
        _ffmpeg = ffmpeg;

        _preview.ApplySettings(_settings);
        ShowPreviewFor("photo.jpg", FileKind.Image);
        _preview.SetHot(0);

        // The preview behaves like the real wheel: hovering lights buttons, clicking one flashes it,
        // and clicking the centre replays the opening animation.
        _preview.MouseMove += (_, e) => _preview.SetHot(_preview.HitTest(e.GetPosition(_preview)));
        _preview.MouseLeave += (_, _) => _preview.SetHot(-1);
        _preview.MouseLeftButtonUp += (_, e) =>
        {
            int index = _preview.HitTest(e.GetPosition(_preview));
            if (index == WheelView.CenterIndex)
                _preview.PlayIntro();
            else
                _preview.Flash(index);
        };
        PreviewImage.Checked += (_, _) => ShowPreviewFor("photo.jpg", FileKind.Image);
        PreviewVideo.Checked += (_, _) => ShowPreviewFor("clip.mp4", FileKind.Video);
        PreviewAudio.Checked += (_, _) => ShowPreviewFor("song.mp3", FileKind.Audio);
        PreviewDocument.Checked += (_, _) => ShowPreviewFor("report.docx", FileKind.Document);
        PreviewHost.Content = _preview;

        VersionText.Text = $"Circularity {Diagnostics.AppVersion()}";

        // Looking for FFmpeg, Office and the rest takes a moment, so only when the tab is first opened.
        Tabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source == Tabs && Tabs.SelectedItem == AboutTab && _diagnostics == null)
            {
                var items = await Diagnostics.CollectAsync(_ffmpeg);
                // Back on this window's thread, whatever thread the await resumed on.
                Dispatcher.Invoke(() =>
                {
                    _diagnostics = items;
                    ShowDiagnostics(items);
                });
            }
        };

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
        RefreshButtons();
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

    /// <summary>Shows the buttons the real wheel would offer for a file like this one, and lists them for rearranging.</summary>
    private void ShowPreviewFor(string sampleFile, FileKind kind)
    {
        // Called by RadioButton.Checked while InitializeComponent is still running.
        if (_preview == null || _canHandle == null)
            return;
        _sample = sampleFile;
        _kind = kind;
        RefreshButtons();
    }

    /// <summary>Puts in settings that changed elsewhere (a button saved from the Resize window, say).</summary>
    public void Reload(Settings settings)
    {
        _settings = settings.Clone();
        LoadControls();
    }

    // --- Buttons tab ---

    private void RefreshButtons()
    {
        var (label, actions) = Formats.ActionsFor(new[] { _sample }, _canHandle, _settings);
        // The sample isn't a real file, so it gets a typical size to show what the wheel's centre says.
        string size = _kind switch { FileKind.Image => "3.1 MB", FileKind.Video => "48 MB", FileKind.Audio => "7.2 MB", _ => "220 KB" };
        if (actions.Length > 0)
            _preview.SetActions(actions, _sample, size);
        else
            _preview.SetActions(actions, label);
        BuildButtonList(actions);
    }

    /// <summary>
    /// Every button the wheel can show for the previewed kind of file, in order, each with where it
    /// ends up for the sample file, arrows to move it, a box to show or hide it, and (for saved
    /// buttons) a way to delete it.
    /// </summary>
    private void BuildButtonList(WheelAction[] shown)
    {
        ButtonsHeading.Text = _kind switch
        {
            FileKind.Image => "Buttons for images",
            FileKind.Video => "Buttons for videos",
            FileKind.Audio => "Buttons for audio",
            _ => "Buttons for documents",
        };
        ResetOrderButton.IsEnabled = _settings.ButtonLayouts.ContainsKey(_kind.ToString());

        var (ring, arc) = WheelView.Layout(shown);
        var onRing = ring.Where(i => i >= 0).Select(i => shown[i].Id).ToHashSet();
        var inArc = arc.Select(i => shown[i].Id).ToHashSet();
        var hidden = Layout(create: false)?.Hidden ?? new List<string>();
        string ext = Formats.NormalizedExtension(_sample);

        var all = Formats.AllButtonsFor(_kind, _settings);
        ButtonList.Children.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            var action = all[i];
            string id = action.Id;
            string where = hidden.Contains(id) ? "Hidden"
                : onRing.Contains(id) ? "On the wheel"
                : inArc.Contains(id) ? "Behind More"
                : action.Kind == WheelActionKind.Combine ? "When several files are dropped"
                : action.Kind == WheelActionKind.Compress && _kind == FileKind.Document ? "For PDFs"
                : action.Kind == WheelActionKind.Convert && !Formats.Offers(_sample, action.Target!) ? $"Not for .{ext} files"
                : "Not available on this PC";
            ButtonList.Children.Add(ButtonRow(action, where, i, all.Count, !hidden.Contains(id)));
        }
    }

    private FrameworkElement ButtonRow(WheelAction action, string where, int index, int count, bool visible)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 4, 6), LastChildFill = true };

        var show = new CheckBox { IsChecked = visible, ToolTip = "Show on the wheel", MinWidth = 0, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        show.Click += (_, _) => SetHidden(action.Id, show.IsChecked != true);
        DockPanel.SetDock(show, Dock.Right);
        row.Children.Add(show);

        if (action.Preset != null)
        {
            var delete = IconButton("", $"Delete “{action.Label}”");
            delete.Click += (_, _) => DeletePreset(action.Preset);
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
        }

        var down = IconButton("", "Move down");
        down.IsEnabled = index < count - 1;
        down.Click += (_, _) => Move(action.Id, +1);
        DockPanel.SetDock(down, Dock.Right);
        row.Children.Add(down);

        var up = IconButton("", "Move up");
        up.IsEnabled = index > 0;
        up.Click += (_, _) => Move(action.Id, -1);
        DockPanel.SetDock(up, Dock.Right);
        row.Children.Add(up);

        var icon = new TextBlock { Text = action.Glyph, FontFamily = IconFont, FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        row.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = visible ? 1 : 0.55,
            Children =
            {
                new TextBlock { Text = action.Preset != null ? $"{action.Label}  ·  saved {action.Preset.Operation.Describe().ToLowerInvariant()}" : action.Label },
                new TextBlock { Text = where, FontSize = 11.5, Opacity = 0.65 },
            },
        });
        return row;
    }

    private static Button IconButton(string glyph, string tooltip) => new()
    {
        Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 12 },
        ToolTip = tooltip,
        Width = 32,
        Height = 30,
        Padding = new Thickness(0),
        Margin = new Thickness(4, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The layout for the previewed kind of file, made when asked to.</summary>
    private ButtonLayout? Layout(bool create)
    {
        string key = _kind.ToString();
        if (!_settings.ButtonLayouts.TryGetValue(key, out var layout) && create)
            _settings.ButtonLayouts[key] = layout = new ButtonLayout();
        return layout;
    }

    private void Move(string id, int by)
    {
        var order = Formats.AllButtonsFor(_kind, _settings).Select(a => a.Id).ToList();
        int at = order.IndexOf(id), to = at + by;
        if (at < 0 || to < 0 || to >= order.Count)
            return;
        (order[at], order[to]) = (order[to], order[at]);
        Layout(create: true)!.Order = order;
        ButtonsChanged();
    }

    private void SetHidden(string id, bool hide)
    {
        var layout = Layout(create: true)!;
        layout.Hidden.Remove(id);
        if (hide)
            layout.Hidden.Add(id);
        ButtonsChanged();
    }

    private void DeletePreset(Preset preset)
    {
        _settings.Presets.RemoveAll(p => p.Id == preset.Id);
        string id = "preset:" + preset.Id;
        foreach (var layout in _settings.ButtonLayouts.Values)
        {
            layout.Order.Remove(id);
            layout.Hidden.Remove(id);
        }
        ButtonsChanged();
    }

    private void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        _settings.ButtonLayouts.Remove(_kind.ToString());
        ButtonsChanged();
    }

    private void ButtonsChanged()
    {
        RefreshButtons();
        SettingsChanged?.Invoke(_settings.Clone());
    }

    // --- About tab ---

    private void ShowDiagnostics(List<DiagnosticItem> items)
    {
        DiagnosticsList.Children.Clear();
        foreach (var item in items)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var mark = new TextBlock
            {
                Text = item.Ok ? "" : "", // check mark / warning
                FontFamily = IconFont,
                FontSize = 14,
                Width = 26,
                Foreground = new SolidColorBrush(item.Ok ? Color.FromRgb(0x2E, 0xA0, 0x43) : Color.FromRgb(0xD9, 0x7A, 0x06)),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = item.Detail, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                },
            });
            DiagnosticsList.Children.Add(row);
        }
    }

    private async void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        CopyDiagnosticsButton.IsEnabled = false;
        try
        {
            var items = _diagnostics ?? await Diagnostics.CollectAsync(_ffmpeg);
            string report = await System.Threading.Tasks.Task.Run(() => Diagnostics.Report(items));
            Dispatcher.Invoke(() =>
            {
                _diagnostics = items;
                ShowDiagnostics(items);
                Clipboard.SetText(report);
                CopiedText.Visibility = Visibility.Visible;
            });
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Another app can briefly hold the clipboard.
            MessageBox.Show(this, $"Couldn't copy to the clipboard: {ex.Message}", "Circularity");
        }
        finally
        {
            CopyDiagnosticsButton.IsEnabled = true;
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (System.IO.File.Exists(Log.FilePath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
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
        // Not preferences, so resetting shouldn't bring the welcome back or delete the user's buttons.
        _settings = new Settings { WelcomeShown = _settings.WelcomeShown, Presets = _settings.Presets };
        LoadControls();
        SettingsChanged?.Invoke(_settings.Clone());
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private void CheckForUpdates_Click(object sender, RoutedEventArgs e) => CheckForUpdatesRequested?.Invoke();

    private void SourceCode_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://github.com/{UpdateChecker.Repository}") { UseShellExecute = true });
}
