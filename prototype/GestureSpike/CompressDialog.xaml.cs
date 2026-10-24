using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GestureSpike.Engine;

namespace GestureSpike;

/// <summary>Asks how to compress images: a JPEG quality, or a file size to fit under.</summary>
internal partial class CompressDialog : Window
{
    private static readonly (string Label, double Value, int Unit)[] PresetSizes =
    {
        ("100 KB", 100, 0), ("250 KB", 250, 0), ("500 KB", 500, 0), ("1 MB", 1, 1), ("2 MB", 2, 1),
    };

    /// <summary>The chosen compression, once the dialog has returned true.</summary>
    public CompressSpec? Result { get; private set; }

    public CompressDialog(string[] files)
    {
        InitializeComponent();

        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        Heading.Text = files.Length == 1 ? $"Compress {Path.GetFileName(files[0])}" : $"Compress {files.Length} images";
        SubHeading.Text = files.Length == 1
            ? $"Currently {FileSizes.Format(totalBytes)}"
            : $"{FileSizes.Format(totalBytes)} in total";
        SizeHint.Text = files.Length == 1
            ? "Keeps the best quality that fits. If needed, the picture is also scaled down."
            : "Each image is compressed to at most this size, keeping the best quality that fits.";

        // Formats that can carry transparency; JPEG can't.
        AlphaNote.Visibility = files.Any(f => Formats.NormalizedExtension(f) is "png" or "gif" or "tiff" or "webp" or "ico")
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var (label, value, unit) in PresetSizes)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 0), MinWidth = 64 };
            button.Click += (_, _) =>
            {
                SizeBox.Text = value.ToString(CultureInfo.CurrentCulture);
                UnitBox.SelectedIndex = unit;
            };
            SizePresets.Children.Add(button);
        }

        QualityMode.Checked += (_, _) => Update();
        SizeMode.Checked += (_, _) => Update();
        QualitySlider.ValueChanged += (_, _) => Update();
        SizeBox.TextChanged += (_, _) => Update();
        UnitBox.SelectionChanged += (_, _) => Update();

        Loaded += (_, _) => WindowHelpers.ForceForeground(this);
        Update();
    }

    private void Update()
    {
        // Called by events that can fire while InitializeComponent is still building the controls.
        if (OkButton == null || QualityText == null || SizePanel == null)
            return;

        QualityPanel.Visibility = QualityMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SizePanel.Visibility = SizeMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        QualityText.Text = $"{QualitySlider.Value:0}%";
        OkButton.IsEnabled = BuildSpec() != null;
    }

    private CompressSpec? BuildSpec()
    {
        if (QualityMode.IsChecked == true)
            return new CompressSpec(Quality: (int)QualitySlider.Value);

        if (!double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out double amount) || amount <= 0)
            return null;
        long bytes = (long)Math.Round(amount * (UnitBox.SelectedIndex == 1 ? FileSizes.MB : FileSizes.KB));
        return bytes >= 5 * FileSizes.KB ? new CompressSpec(MaxBytes: bytes) : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildSpec();
        if (Result != null)
            DialogResult = true;
    }
}
