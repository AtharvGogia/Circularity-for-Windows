using System;
using System.Globalization;
using System.IO;
using System.Windows;
using GestureSpike.Engine;

namespace GestureSpike;

/// <summary>Asks for a new width × height, optionally keeping the aspect ratio.</summary>
internal partial class ResizeDialog : Window
{
    private readonly (int Width, int Height)? _source;
    private bool _syncing;

    /// <summary>The chosen resize, once the dialog has returned true.</summary>
    public ResizeSpec? Result { get; private set; }

    /// <param name="sourceSize">Upright size of the file, when exactly one file is being resized.</param>
    public ResizeDialog(string[] files, (int Width, int Height)? sourceSize)
    {
        InitializeComponent();
        _source = sourceSize;

        Heading.Text = files.Length == 1 ? $"Resize {Path.GetFileName(files[0])}" : $"Resize {files.Length} files";
        SubHeading.Text = sourceSize is { } s
            ? $"Currently {s.Width} × {s.Height} px"
            : "With “Keep aspect ratio” on, each file is scaled to fit inside this size.";

        var (w, h) = sourceSize ?? (1920, 1080);
        WidthBox.Text = w.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = h.ToString(CultureInfo.InvariantCulture);

        WidthBox.TextChanged += (_, _) => SyncAspect(fromWidth: true);
        HeightBox.TextChanged += (_, _) => SyncAspect(fromWidth: false);
        KeepAspect.Click += (_, _) => Update();

        Loaded += (_, _) =>
        {
            WindowHelpers.ForceForeground(this);
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
        Update();
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

    private void Update()
    {
        // Called by events that can fire while InitializeComponent is still building the controls.
        if (OkButton == null || ResultText == null)
            return;

        var spec = BuildSpec();
        OkButton.IsEnabled = spec != null;
        if (spec == null)
        {
            ResultText.Text = "Enter a width and height between 1 and 20000.";
        }
        else if (_source is { } s)
        {
            var (w, h) = spec.Apply(s.Width, s.Height);
            ResultText.Text = $"New size: {w} × {h} px";
        }
        else
        {
            ResultText.Text = spec.Kind == ResizeKind.Fit
                ? "Each file keeps its shape and fits inside this size."
                : "Each file is stretched to exactly this size.";
        }
    }

    private ResizeSpec? BuildSpec() =>
        TryInt(WidthBox.Text, out int w) && TryInt(HeightBox.Text, out int h)
            ? new ResizeSpec(KeepAspect.IsChecked == true ? ResizeKind.Fit : ResizeKind.Stretch, w, h)
            : null;

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value is >= 1 and <= 20000;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildSpec();
        if (Result != null)
            DialogResult = true;
    }
}
