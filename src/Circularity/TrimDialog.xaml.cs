using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Circularity.Engine;

namespace Circularity;

/// <summary>
/// Picks the part of a video or audio file to keep: a start and an end, by slider or by typing a time,
/// with the frame at each cut point shown for videos.
/// </summary>
internal partial class TrimDialog : Window
{
    /// <summary>The shortest piece that can be kept.</summary>
    private static readonly TimeSpan MinimumLength = TimeSpan.FromSeconds(0.1);

    private readonly TimeSpan _length;
    private readonly Func<TimeSpan, Task<ImageSource?>>? _frameAt;
    private readonly DispatcherTimer _startPreview, _endPreview;
    private int _startRequest, _endRequest;
    private bool _syncing;

    /// <summary>The chosen trim, once the dialog has returned true.</summary>
    public TrimTo? Result { get; private set; }

    /// <param name="position">"2 of 3" when several files are trimmed one after another, else null.</param>
    /// <param name="frameAt">Gets the video frame at a time, for the previews; null for audio.</param>
    public TrimDialog(string file, TimeSpan length, string? position, Func<TimeSpan, Task<ImageSource?>>? frameAt)
    {
        InitializeComponent();
        _length = length;
        _frameAt = frameAt;

        Heading.Text = $"Trim {Path.GetFileName(file)}";
        SubHeading.Text = $"{Times.FormatPrecise(length)} long" + (position != null ? $" · {position}" : "");
        if (frameAt == null)
        {
            Previews.Visibility = Visibility.Collapsed;
            QuickCutText.Text = "Instant, with no loss in quality.";
        }

        foreach (var slider in new[] { StartSlider, EndSlider })
        {
            slider.Minimum = 0;
            slider.Maximum = length.TotalSeconds;
        }
        StartSlider.Value = 0;
        EndSlider.Value = length.TotalSeconds;

        // Previews follow the sliders once they settle, rather than decoding a frame for every pixel moved.
        _startPreview = Debounced(() => ShowFrame(StartFrame, TimeSpan.FromSeconds(StartSlider.Value), ++_startRequest, () => _startRequest));
        _endPreview = Debounced(() => ShowFrame(EndFrame, TimeSpan.FromSeconds(EndSlider.Value), ++_endRequest, () => _endRequest));

        StartSlider.ValueChanged += (_, _) => SliderMoved(isStart: true);
        EndSlider.ValueChanged += (_, _) => SliderMoved(isStart: false);
        StartBox.LostKeyboardFocus += (_, _) => Typed(StartBox, isStart: true);
        EndBox.LostKeyboardFocus += (_, _) => Typed(EndBox, isStart: false);
        StartBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(StartBox, isStart: true); };
        EndBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Typed(EndBox, isStart: false); };

        Loaded += (_, _) =>
        {
            WindowHelpers.ForceForeground(this);
            _startPreview.Start();
            _endPreview.Start();
        };
        Update();
    }

    private DispatcherTimer Debounced(Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    private async void ShowFrame(Image target, TimeSpan at, int request, Func<int> latest)
    {
        if (_frameAt == null)
            return;
        var frame = await _frameAt(at);
        // A newer request may have finished first; only the latest one gets shown.
        if (request == latest() && frame != null)
            target.Source = frame;
    }

    private void SliderMoved(bool isStart)
    {
        if (_syncing || StartSlider == null || EndSlider == null)
            return;
        _syncing = true;
        // Keep the start before the end, pushing the other one along if needed.
        double gap = MinimumLength.TotalSeconds;
        if (isStart && StartSlider.Value > EndSlider.Value - gap)
            EndSlider.Value = Math.Min(_length.TotalSeconds, StartSlider.Value + gap);
        if (!isStart && EndSlider.Value < StartSlider.Value + gap)
            StartSlider.Value = Math.Max(0, EndSlider.Value - gap);
        _syncing = false;

        (isStart ? _startPreview : _endPreview).Stop();
        (isStart ? _startPreview : _endPreview).Start();
        Update();
    }

    private void Typed(TextBox box, bool isStart)
    {
        var slider = isStart ? StartSlider : EndSlider;
        if (Times.TryParse(box.Text, out var time))
            slider.Value = Math.Clamp(time.TotalSeconds, 0, _length.TotalSeconds);
        // Show it back neatly (or undo a typo).
        box.Text = Times.FormatPrecise(TimeSpan.FromSeconds(slider.Value));
    }

    private void Update()
    {
        // Called by events that can fire while InitializeComponent is still building the controls.
        if (OkButton == null || KeepText == null)
            return;
        var start = TimeSpan.FromSeconds(StartSlider.Value);
        var end = TimeSpan.FromSeconds(EndSlider.Value);
        if (!StartBox.IsKeyboardFocused)
            StartBox.Text = Times.FormatPrecise(start);
        if (!EndBox.IsKeyboardFocused)
            EndBox.Text = Times.FormatPrecise(end);

        bool whole = start < TimeSpan.FromSeconds(0.05) && end > _length - TimeSpan.FromSeconds(0.05);
        KeepText.Text = whole ? "Move the start or end to choose what to keep." : $"Keeps {Times.FormatPrecise(end - start)} of {Times.FormatPrecise(_length)}.";
        OkButton.IsEnabled = !whole && end - start >= MinimumLength;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = new TrimTo(TimeSpan.FromSeconds(StartSlider.Value), TimeSpan.FromSeconds(EndSlider.Value), Exact: ExactCut.IsChecked == true);
        DialogResult = true;
    }
}
