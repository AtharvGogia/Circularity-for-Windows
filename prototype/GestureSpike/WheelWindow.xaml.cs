using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GestureSpike;

/// <summary>
/// The radial format picker. It is a real OLE drop target: the user keeps dragging
/// their file(s) onto a wedge and releases, and we receive the files through a normal drop.
/// The centre of the wheel is a cancel zone, which is where the cursor starts.
/// </summary>
public partial class WheelWindow : Window
{
    private const double Diameter = 340;
    private const double Center = Diameter / 2;
    private const double OuterRadius = 165;
    private const double InnerRadius = 48;

    private static readonly Brush DiscBrush = Frozen(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x22));
    private static readonly Brush WedgeBrush = Frozen(Color.FromArgb(0xFF, 0x2C, 0x2C, 0x36));
    private static readonly Brush HotBrush = Frozen(Color.FromArgb(0xFF, 0xFF, 0x8A, 0x1E));
    private static readonly Brush TextBrush = Frozen(Colors.White);
    private static readonly Brush DimTextBrush = Frozen(Color.FromArgb(0xFF, 0xA0, 0xA0, 0xAA));

    /// <summary>Raised when files are dropped on a wedge.</summary>
    public event Action<IReadOnlyList<string>, string>? FormatChosen;

    private readonly List<Path> _wedges = new();
    private readonly TextBlock _centerText;
    private readonly DispatcherTimer _hideTimer;
    private string[] _targets = Array.Empty<string>();
    private int _hot = -1;
    private bool _dropped;
    private IntPtr _hwnd;

    public WheelWindow()
    {
        InitializeComponent();

        _centerText = new TextBlock
        {
            Foreground = DimTextBrush,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Width = InnerRadius * 2 - 8,
            IsHitTestVisible = false,
        };

        // After the mouse button comes up, wait briefly for the drop to arrive before hiding.
        // The hook sees the button release before OLE delivers the drop to us.
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_dropped)
            {
                Log.Write("Drag ended without a drop on the wheel.");
                Hide();
            }
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };

        Rebuild();
    }

    /// <summary>Shows the wheel centred on a point given in physical screen pixels.</summary>
    public void ShowAt(int x, int y)
    {
        _hideTimer.Stop();
        _dropped = false;
        _hot = -1;
        SetTargets(Array.Empty<string>(), "Drag onto\na format");

        // Show invisibly, move with Win32 (physical pixels), then reveal. Moving to a monitor with a
        // different DPI makes WPF resize the window, so centre once more after that settles.
        Opacity = 0;
        Show();
        CenterOn(x, y);
        Opacity = 1;
        Dispatcher.BeginInvoke(() => CenterOn(x, y), DispatcherPriority.Loaded);
    }

    /// <summary>Called when the user releases the left button after a chord.</summary>
    public void OnDragEnded()
    {
        if (IsVisible)
            _hideTimer.Start();
    }

    private void CenterOn(int x, int y)
    {
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x - w / 2, y - h / 2, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        var files = GetFiles(e);
        if (files == null)
        {
            SetTargets(Array.Empty<string>(), "Not a file");
        }
        else
        {
            var (kind, targets) = Formats.For(files);
            string label = kind switch
            {
                FileKind.Mixed => "Mixed file types",
                FileKind.Unknown => "Unsupported type",
                _ => files.Length == 1 ? kind.ToString() : $"{files.Length} {kind.ToString().ToLowerInvariant()} files",
            };
            SetTargets(targets, label);
        }
        UpdateHot(e);
    }

    protected override void OnDragOver(DragEventArgs e) => UpdateHot(e);

    protected override void OnDragLeave(DragEventArgs e) => SetHot(-1);

    protected override void OnDrop(DragEventArgs e)
    {
        _dropped = true;
        _hideTimer.Stop();
        e.Handled = true;

        var files = GetFiles(e);
        int index = HitTest(e.GetPosition(Root));
        Hide();

        if (files != null && index >= 0)
        {
            Log.Write($"Dropped {files.Length} file(s) on '{_targets[index]}': {string.Join(", ", files)}");
            FormatChosen?.Invoke(files, _targets[index]);
        }
        else
        {
            Log.Write("Dropped on the cancel zone, nothing converted.");
        }
    }

    private void UpdateHot(DragEventArgs e)
    {
        int index = GetFiles(e) == null ? -1 : HitTest(e.GetPosition(Root));
        SetHot(index);

        // Always Copy, never Move: a Move result would tell Explorer to delete the original.
        e.Effects = index >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static string[]? GetFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    /// <summary>Which wedge a point is over, or -1 for the centre/outside.</summary>
    private int HitTest(Point p)
    {
        if (_targets.Length == 0)
            return -1;

        double dx = p.X - Center, dy = p.Y - Center;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r < InnerRadius || r > OuterRadius)
            return -1;

        // Angle measured clockwise from 12 o'clock, with wedge 0 centred at the top.
        double step = 360.0 / _targets.Length;
        double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        return (int)(((angle + step / 2) % 360) / step);
    }

    private void SetTargets(string[] targets, string centerLabel)
    {
        _centerText.Text = centerLabel;
        if (targets.SequenceEqual(_targets) && Root.Children.Count > 0)
            return;

        _targets = targets;
        _hot = -1;
        Rebuild();
    }

    private void SetHot(int index)
    {
        if (index == _hot)
            return;
        if (_hot >= 0 && _hot < _wedges.Count)
            _wedges[_hot].Fill = WedgeBrush;
        if (index >= 0 && index < _wedges.Count)
            _wedges[index].Fill = HotBrush;
        _hot = index;
    }

    private void Rebuild()
    {
        Root.Children.Clear();
        _wedges.Clear();

        // The base disc is the only hit-testable element. Nearly-transparent pixels in a layered
        // window don't receive input, so it needs a visible fill.
        var disc = new Ellipse { Width = OuterRadius * 2, Height = OuterRadius * 2, Fill = DiscBrush };
        Canvas.SetLeft(disc, Center - OuterRadius);
        Canvas.SetTop(disc, Center - OuterRadius);
        Root.Children.Add(disc);

        int n = _targets.Length;
        double step = n == 0 ? 360 : 360.0 / n;
        for (int i = 0; i < n; i++)
        {
            var wedge = new Path
            {
                Data = n == 1 ? Ring() : Wedge(i * step - step / 2, i * step + step / 2),
                Fill = WedgeBrush,
                Stroke = DiscBrush,
                StrokeThickness = 3,
                IsHitTestVisible = false,
            };
            Root.Children.Add(wedge);
            _wedges.Add(wedge);

            var label = new TextBlock
            {
                Text = _targets[i].ToUpperInvariant(),
                Foreground = TextBrush,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                IsHitTestVisible = false,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var mid = PointAt(i * step, (InnerRadius + OuterRadius) / 2);
            Canvas.SetLeft(label, mid.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, mid.Y - label.DesiredSize.Height / 2);
            Root.Children.Add(label);
        }

        _centerText.Measure(new Size(_centerText.Width, double.PositiveInfinity));
        Canvas.SetLeft(_centerText, Center - _centerText.Width / 2);
        Canvas.SetTop(_centerText, Center - _centerText.DesiredSize.Height / 2);
        Root.Children.Add(_centerText);
    }

    /// <summary>A point at an angle (degrees clockwise from 12 o'clock) and radius from the centre.</summary>
    private static Point PointAt(double degrees, double radius)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(Center + radius * Math.Sin(rad), Center - radius * Math.Cos(rad));
    }

    private static Geometry Wedge(double fromDeg, double toDeg)
    {
        bool large = toDeg - fromDeg > 180;
        var figure = new PathFigure { StartPoint = PointAt(fromDeg, OuterRadius), IsClosed = true };
        figure.Segments.Add(new ArcSegment(PointAt(toDeg, OuterRadius), new Size(OuterRadius, OuterRadius), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(PointAt(toDeg, InnerRadius), true));
        figure.Segments.Add(new ArcSegment(PointAt(fromDeg, InnerRadius), new Size(InnerRadius, InnerRadius), 0, large, SweepDirection.Counterclockwise, true));
        return new PathGeometry(new[] { figure });
    }

    private static Geometry Ring() => new GeometryGroup
    {
        FillRule = FillRule.EvenOdd,
        Children =
        {
            new EllipseGeometry(new Point(Center, Center), OuterRadius, OuterRadius),
            new EllipseGeometry(new Point(Center, Center), InnerRadius, InnerRadius),
        },
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
