using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Circularity.Engine;

namespace Circularity;

/// <summary>Asks for a new width × height, optionally keeping the aspect ratio.</summary>
internal partial class ResizeDialog : Window
{
    private readonly (int Width, int Height)? _source;
    private bool _syncing;

    /// <summary>The chosen resize, once the dialog has returned true.</summary>
    public ResizeSpec? Result { get; private set; }

    /// <summary>The name for a wheel button that repeats this resize, if the user asked for one.</summary>
    public string? PresetName { get; private set; }

    private readonly PresetNamer _namer;

    /// <param name="sourceSize">Upright size of the file, when exactly one file is being resized.</param>
    public ResizeDialog(string[] files, (int Width, int Height)? sourceSize)
    {
        InitializeComponent();
        _source = sourceSize;

        Heading.Text = files.Length == 1 ? $"Resize {Path.GetFileName(files[0])}" : $"Resize {files.Length} files";
        SubHeading.Text = sourceSize is { } s
            ? $"Currently {s.Width} × {s.Height} px"
            : "With “Fit inside” mode, each file is scaled to fit inside this size.";

        var (w, h) = sourceSize ?? (1920, 1080);
        WidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = h.ToString(CultureInfo.InvariantCulture);

        // Set default colors
        ColorPicker.Color = Colors.White;
        GradientStartPicker.Color = Colors.LightGray;
        GradientEndPicker.Color = Colors.White;

        // Attach color picker event handlers (can't use XAML for generic RoutedEvent)
        ColorPicker.ColorChanged += Color_Changed;
        GradientStartPicker.ColorChanged += Color_Changed;
        GradientEndPicker.ColorChanged += Color_Changed;

        _namer = new PresetNamer(SaveAsPreset, PresetNamePanel, PresetNameBox, Update);
        WidthBox.TextChanged += (_, _) => SyncAspect(fromWidth: true);
        HeightBox.TextChanged += (_, _) => SyncAspect(fromWidth: false);
        KeepAspect.Click += (_, _) => Update();
        ModeBox.SelectionChanged += (_, _) => UpdateModeUI();

        Loaded += (_, _) =>
        {
            WindowHelpers.ForceForeground(this);
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
        Update();
        UpdateModeUI();
    }

    /// <summary>With one file and "keep aspect ratio" on, typing a width fills in the height and vice versa.</summary>
    private void SyncAspect(bool fromWidth)
    {
        if (!_syncing && _source is { } s && KeepAspect.IsChecked == true)
        {
            _syncing = true;
            if (fromWidth && TryInt(WidthBox.Text, out int w))
                HeightBox.Text = Math.Max(1, (int)Math.Round((double)w * s.Height / s.Width)).ToString(CultureInfo.InvariantCulture);
            else if (!fromWidth && TryInt(HeightBox.Text, out int h))
                WidthBox.Text = Math.Max(1, (int)Math.Round((double)h * s.Width / s.Height)).ToString(CultureInfo.InvariantCulture);
            _syncing = false;
        }
        Update();
    }

    private void UpdateModeUI()
    {
        // UI elements may not be initialized yet during InitializeComponent()
        if (BackgroundPanel == null || ScalePanel == null || KeepAspect == null)
            return;

        bool isFitWithBackground = GetMode() == ResizeKind.FitWithBackground;
        bool isFitOrFitBackground = GetMode() != ResizeKind.Stretch;

        BackgroundPanel.Visibility = isFitWithBackground ? Visibility.Visible : Visibility.Collapsed;
        ScalePanel.Visibility = isFitWithBackground ? Visibility.Visible : Visibility.Collapsed;
        KeepAspect.Visibility = Visibility.Collapsed; // Now handled by mode selection

        // Sync aspect ratio only for Fit and FitWithBackground modes
        KeepAspect.IsChecked = isFitOrFitBackground;

        UpdateBackgroundKindUI();
    }

    private void UpdateBackgroundKindUI()
    {
        // UI elements may not be initialized yet during InitializeComponent()
        if (ColorPanel == null || GradientPanel == null || BlurPanel == null)
            return;

        var kind = GetBackgroundKind();
        ColorPanel.Visibility = kind == ResizeBackgroundKind.Color ? Visibility.Visible : Visibility.Collapsed;
        GradientPanel.Visibility = kind == ResizeBackgroundKind.Gradient ? Visibility.Visible : Visibility.Collapsed;
        BlurPanel.Visibility = kind == ResizeBackgroundKind.BlurredSource ? Visibility.Visible : Visibility.Collapsed;
    }

    private ResizeKind GetMode()
    {
        if (ModeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return Enum.Parse<ResizeKind>(tag);
        return ResizeKind.Fit;
    }

    private ResizeBackgroundKind GetBackgroundKind()
    {
        if (BackgroundKindBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return Enum.Parse<ResizeBackgroundKind>(tag);
        return ResizeBackgroundKind.Color;
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeUI();
        Update();
    }

    private void BackgroundKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateBackgroundKindUI();
        Update();
    }

    private void Color_Changed(object sender, RoutedPropertyChangedEventArgs<Color> e)
    {
        UpdateGradientPreview();
        Update();
    }

    private void GradientPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GradientPresetBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrEmpty(tag))
        {
            var parts = tag.Split(';');
            if (parts.Length == 2 &&
                ColorConverter.ConvertFromString(parts[0]) is Color start &&
                ColorConverter.ConvertFromString(parts[1]) is Color end)
            {
                GradientStartPicker.Color = start;
                GradientEndPicker.Color = end;
                UpdateGradientPreview();
            }
        }
        // "Custom" has no tag - do nothing
    }

    private void GradientAngle_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GradientAngleValueText != null)
            GradientAngleValueText.Text = $"{(int)e.NewValue}°";
        UpdateGradientPreview();
        Update();
    }

    private void UpdateGradientPreview()
    {
        if (GradientPreviewBrush == null || GradientPreviewStart == null || GradientPreviewEnd == null)
            return;

        GradientPreviewStart.Color = GradientStartPicker?.Color ?? Colors.White;
        GradientPreviewEnd.Color = GradientEndPicker?.Color ?? Colors.LightGray;

        // Convert angle to StartPoint/EndPoint
        double angle = GradientAngleSlider?.Value ?? 0;
        double rad = angle * Math.PI / 180.0;

        // Start point at angle direction, end point opposite
        double startX = 0.5 + 0.5 * Math.Cos(rad);
        double startY = 0.5 - 0.5 * Math.Sin(rad);
        double endX = 0.5 - 0.5 * Math.Cos(rad);
        double endY = 0.5 + 0.5 * Math.Sin(rad);

        GradientPreviewBrush.StartPoint = new Point(startX, startY);
        GradientPreviewBrush.EndPoint = new Point(endX, endY);
    }

    private void Blur_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlurValueText != null)
            BlurValueText.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
        Update();
    }

    private void Scale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleValueText != null)
            ScaleValueText.Text = $"{e.NewValue:P0}";
        Update();
    }

    private void Update()
    {
        // Called by events that can fire while InitializeComponent is still building the controls.
        if (OkButton == null || ResultText == null || _namer == null)
            return;

        var spec = BuildSpec();
        OkButton.IsEnabled = spec != null && _namer.IsValid;
        if (spec != null)
            _namer.Suggest($"{spec.Width}×{spec.Height}");
        if (spec == null)
        {
            ResultText.Text = "Enter a width and height between 1 and 20000.";
        }
        else if (_source is { } s)
        {
            var (fw, fh) = spec.FittedSize(s.Width, s.Height);
            var (w, h) = spec.Apply(s.Width, s.Height);
            string modeDesc = spec.Kind switch
            {
                ResizeKind.Fit => "Each file keeps its shape and fits inside this size.",
                ResizeKind.Stretch => "Each file is stretched to exactly this size.",
                ResizeKind.FitWithBackground => $"Fitted size: {fw} × {fh} px on {spec.Width} × {spec.Height} px canvas.",
                _ => ""
            };
            ResultText.Text = $"New canvas: {w} × {h} px. {modeDesc}";
        }
        else
        {
            string modeDesc = spec.Kind switch
            {
                ResizeKind.Fit => "Each file keeps its shape and fits inside this size.",
                ResizeKind.Stretch => "Each file is stretched to exactly this size.",
                ResizeKind.FitWithBackground => "Each file keeps its shape, fitted on a canvas with background.",
                _ => ""
            };
            ResultText.Text = $"Canvas: {spec.Width} × {spec.Height} px. {modeDesc}";
        }
    }

    private ResizeSpec? BuildSpec()
    {
        if (!TryInt(WidthBox.Text, out int w) || !TryInt(HeightBox.Text, out int h))
            return null;

        var mode = GetMode();

        if (mode == ResizeKind.FitWithBackground)
        {
            var bgKind = GetBackgroundKind();
            ResizeBackground bg = bgKind switch
            {
                ResizeBackgroundKind.Color => ResizeBackground.Solid(ColorPicker.Color),
                ResizeBackgroundKind.Gradient => ResizeBackground.Gradient(GradientStartPicker.Color, GradientEndPicker.Color, GradientAngleSlider?.Value ?? 0),
                ResizeBackgroundKind.BlurredSource => ResizeBackground.Blurred(BlurSlider.Value, ScaleSlider.Value),
                _ => ResizeBackground.Solid(Colors.White)
            };
            return new ResizeSpec(mode, w, h, bg, ScaleSlider.Value);
        }

        if (mode == ResizeKind.Fit)
        {
            return new ResizeSpec(mode, w, h, null, ScaleSlider.Value);
        }

        // Stretch
        return new ResizeSpec(mode, w, h, null, 1.0);
    }

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value is >= 1 and <= 20000;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildSpec();
        PresetName = _namer.Name;
        if (Result != null)
            DialogResult = true;
    }
}
