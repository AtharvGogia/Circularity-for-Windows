using System.IO;
using System.Linq;
using System.Windows;
using Circularity.Engine;

namespace Circularity;

/// <summary>Asks how hard to compress PDFs: light, balanced or strong (see <see cref="PdfConverter.CompressionLevel"/>).</summary>
internal partial class PdfCompressDialog : Window
{
    private readonly PresetNamer _namer;

    /// <summary>The chosen compression, once the dialog has returned true.</summary>
    public CompressSpec? Result { get; private set; }

    /// <summary>The name for a wheel button that repeats this compression, if the user asked for one.</summary>
    public string? PresetName { get; private set; }

    public PdfCompressDialog(string[] files)
    {
        InitializeComponent();
        _namer = new PresetNamer(SaveAsPreset, PresetNamePanel, PresetNameBox, Update);

        long totalBytes = files.Sum(f => new FileInfo(f).Length);
        Heading.Text = files.Length == 1 ? $"Compress {Path.GetFileName(files[0])}" : $"Compress {files.Length} PDFs";
        SubHeading.Text = files.Length == 1 ? $"Currently {FileSizes.Format(totalBytes)}" : $"{FileSizes.Format(totalBytes)} in total";

        foreach (var level in new[] { Light, Balanced, Strong })
            level.Checked += (_, _) => Update();
        Loaded += (_, _) => WindowHelpers.ForceForeground(this);
        Update();
    }

    private int Quality => Light.IsChecked == true ? 80 : Strong.IsChecked == true ? 40 : 60;

    private void Update()
    {
        // Called by events that can fire while InitializeComponent is still building the controls.
        if (OkButton == null || _namer == null)
            return;
        OkButton.IsEnabled = _namer.IsValid;
        _namer.Suggest(Light.IsChecked == true ? "Light PDF" : Strong.IsChecked == true ? "Small PDF" : "Smaller PDF");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new CompressSpec(Quality: Quality);
        PresetName = _namer.Name;
        DialogResult = true;
    }
}
