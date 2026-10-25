using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Circularity.Engine;

namespace Circularity;

/// <summary>
/// Asks how to compress videos: by quality (three levels) or to fit a file size, optionally at a lower
/// resolution. For a single video it shows roughly how big the result will be.
/// </summary>
internal partial class VideoCompressDialog : Window
{
    private static readonly int[] PresetMegabytes = { 8, 10, 25, 50, 100 };
    private static readonly int[] Resolutions = { 1080, 720, 480 };

    private readonly MediaInfo? _info;
    private readonly PresetNamer _namer;

    /// <summary>The chosen compression, once the dialog has returned true.</summary>
    public CompressSpec? Result { get; private set; }

    /// <summary>The name for a wheel button that repeats this compression, if the user asked for one.</summary>
    public string? PresetName { get; private set; }

    /// <param name="info">What the video is like, when exactly one is being compressed; used for the estimate.</param>
    public VideoCompressDialog(string[] files, MediaInfo? info)
    {
        InitializeComponent();
        _info = info;
        _namer = new PresetNamer(SaveAsPreset, PresetNamePanel, PresetNameBox, Update);

        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        Heading.Text = files.Length == 1 ? $"Compress {Path.GetFileName(files[0])}" : $"Compress {files.Length} videos";
        SubHeading.Text = info is { HasVideo: true } v
            ? $"Currently {FileSizes.Format(totalBytes)} · {v.Width} × {v.Height} · {Times.Format(v.Duration)}"
            : $"{FileSizes.Format(totalBytes)} in total";

        foreach (int mb in PresetMegabytes)
        {
            var button = new Button { Content = $"{mb} MB", Margin = new Thickness(0, 0, 6, 0), MinWidth = 56 };
            button.Click += (_, _) => SizeBox.Text = mb.ToString(CultureInfo.CurrentCulture);
            SizePresets.Children.Add(button);
        }

        // "Keep" plus every standard size below the video's own (its shorter side, so portrait clips work too).
        int shortSide = info is { HasVideo: true } s ? Math.Min(s.Width, s.Height) : int.MaxValue;
        ResolutionBox.Items.Add(new ComboBoxItem { Content = info is { HasVideo: true } k ? $"Keep ({k.Width} × {k.Height})" : "Keep", Tag = 0 });
        foreach (int p in Resolutions.Where(p => p < shortSide))
            ResolutionBox.Items.Add(new ComboBoxItem { Content = $"{p}p", Tag = p });
        ResolutionBox.SelectedIndex = 0;

        foreach (var control in new[] { QualityMode, SizeMode, HighQuality, Balanced, Smallest })
            control.Checked += (_, _) => Update();
        SizeBox.TextChanged += (_, _) => Update();
        ResolutionBox.SelectionChanged += (_, _) => Update();

        Loaded += (_, _) => WindowHelpers.ForceForeground(this);
        Update();
    }

    private void Update()
    {
        // Called by events that can fire while InitializeComponent is still building the controls.
        if (OkButton == null || SizePanel == null || EstimateText == null || ResolutionBox == null || _namer == null)
            return;

        QualityPanel.Visibility = QualityMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = SizeMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var spec = BuildSpec();
        OkButton.IsEnabled = spec != null && _namer.IsValid;
        if (spec == null)
        {
            EstimateText.Text = "Enter a size of at least 1 MB.";
            return;
        }

        _namer.Suggest(spec.MaxBytes is { } bytes ? $"Under {FileSizes.Format(bytes)}"
            : (spec.Quality >= 75 ? "High quality" : spec.Quality >= 55 ? "Balanced" : "Small video") + (spec.MaxHeight is { } h ? $" {h}p" : ""));

        if (_info is { HasVideo: true } info)
        {
            var plan = FfmpegConverter.PlanCompression(info, spec);
            EstimateText.Text = spec.MaxBytes != null
                ? $"Will be at most {FileSizes.Format(spec.MaxBytes.Value)}, at {plan.Width} × {plan.Height}."
                : $"Will be about {FileSizes.Format(plan.EstimatedBytes)}, at {plan.Width} × {plan.Height}.";
        }
        else
        {
            EstimateText.Text = "Saved as MP4 (H.264), which plays everywhere.";
        }
    }

    private CompressSpec? BuildSpec()
    {
        int? maxHeight = ResolutionBox.SelectedItem is ComboBoxItem { Tag: int p and > 0 } ? p : null;
        if (QualityMode.IsChecked == true)
            return new CompressSpec(Quality: HighQuality.IsChecked == true ? 80 : Smallest.IsChecked == true ? 40 : 60, MaxHeight: maxHeight);

        if (!double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double mb) || mb < 1)
            return null;
        return new CompressSpec(MaxBytes: (long)Math.Round(mb * FileSizes.MB), MaxHeight: maxHeight);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildSpec();
        PresetName = _namer.Name;
        if (Result != null)
            DialogResult = true;
    }
}
