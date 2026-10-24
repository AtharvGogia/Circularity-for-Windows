using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GestureSpike;

/// <summary>
/// Draws the wheel: a translucent disc whose ring is split into curved, slightly rounded segments
/// (one per button, each with an icon and label) around a cancel button in the middle. Used by the
/// real wheel and by the live preview in Settings.
///
/// The disc is created once and never replaced. It is the only element that takes part in
/// hit-testing, and WPF tracks drag-and-drop by element: if it were rebuilt mid-drag, every mouse
/// move would look like a new "drag enter" and the drop would go to an element that no longer exists.
/// Everything else lives on a separate layer that can be rebuilt freely.
/// </summary>
internal sealed class WheelView : Canvas
{
    /// <summary>Space around the disc for its drop shadow.</summary>
    public const double ShadowMargin = 20;

    /// <summary><see cref="HitTest"/> result for the cancel button in the middle.</summary>
    public const int CenterIndex = -2;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Duration HoverDuration = TimeSpan.FromMilliseconds(110);

    private readonly Ellipse _disc;
    private readonly Canvas _layer = new() { IsHitTestVisible = false };
    private readonly List<WheelButton> _buttons = new();
    private Settings _settings = new();
    private WheelAction[] _actions = Array.Empty<WheelAction>();
    private string _centerLabel = "";
    private Ellipse? _centerDisc;
    private int _hot = -1;

    /// <summary>The drawn parts of one button, so hover can restyle and nudge them.</summary>
    private sealed record WheelButton(Canvas Group, Path Shape, WheelAction Action, double MidAngle);

    public WheelView()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);

        _disc = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        Children.Add(_disc);
        Children.Add(_layer);
        ApplySettings(_settings);
    }

    /// <summary>Width and height of the whole view, including the shadow margin.</summary>
    public double Extent => _settings.WheelSize + 2 * ShadowMargin;

    public IReadOnlyList<WheelAction> Actions => _actions;

    private double Radius => _settings.WheelSize / 2.0;
    private double CenterRadius => _settings.WheelSize * 0.17;
    private double Mid => Extent / 2;

    public void ApplySettings(Settings settings)
    {
        _settings = settings.Clone();
        Width = Height = _layer.Width = _layer.Height = Extent;

        // Same element, new size and colour: see the class comment.
        byte alpha = (byte)Math.Round(_settings.Opacity * 255);
        _disc.Width = _disc.Height = Radius * 2;
        _disc.Fill = new RadialGradientBrush(Color.FromArgb(alpha, 0x2C, 0x2C, 0x34), Color.FromArgb(alpha, 0x14, 0x14, 0x19))
        {
            GradientOrigin = new Point(0.5, 0.35),
        };
        SetLeft(_disc, Mid - Radius);
        SetTop(_disc, Mid - Radius);

        Rebuild();
    }

    public void SetActions(WheelAction[] actions, string centerLabel)
    {
        if (actions.SequenceEqual(_actions) && centerLabel == _centerLabel)
            return;
        if (!actions.SequenceEqual(_actions))
            _hot = -1;
        _actions = actions;
        _centerLabel = centerLabel;
        Rebuild();
    }

    /// <summary>Button index under a point in this view's coordinates, <see cref="CenterIndex"/>, or -1.</summary>
    public int HitTest(Point p)
    {
        double dx = p.X - Mid, dy = p.Y - Mid;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r < CenterRadius)
            return CenterIndex;
        if (r > Radius || _actions.Length == 0)
            return -1;

        // Each button owns its whole slice of the ring, gaps included.
        double step = 360.0 / _actions.Length;
        double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        return (int)(((angle + step / 2) % 360) / step);
    }

    public void SetHot(int index)
    {
        if (index == _hot)
            return;
        Highlight(_hot, false);
        _hot = index;
        Highlight(_hot, true);
    }

    /// <summary>Quick fade-and-grow when the wheel appears.</summary>
    public void PlayIntro()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(140);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        var scale = (ScaleTransform)RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1, duration) { EasingFunction = ease });
    }

    private void Highlight(int index, bool on)
    {
        if (index == CenterIndex && _centerDisc != null)
        {
            _centerDisc.Fill = new SolidColorBrush(on ? Color.FromArgb(0x70, 0xE5, 0x48, 0x4D) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            return;
        }
        if (index < 0 || index >= _buttons.Count)
            return;

        var button = _buttons[index];
        if (on)
        {
            var accent = _settings.Accent;
            Paint(button.Shape, accent);
            TextElement.SetForeground(button.Group, new SolidColorBrush(ContrastText(accent)));
        }
        else
        {
            ApplyRestingStyle(button);
        }

        // The hovered segment slides out slightly along its own direction.
        double push = on ? _settings.WheelSize * 0.018 : 0;
        double rad = button.MidAngle * Math.PI / 180;
        var shift = (TranslateTransform)button.Group.RenderTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(push * Math.Sin(rad), HoverDuration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-push * Math.Cos(rad), HoverDuration) { EasingFunction = ease });
    }

    private void ApplyRestingStyle(WheelButton button)
    {
        var accent = _settings.Accent;
        // Tools are tinted with the accent so they read as a different kind of button.
        Paint(button.Shape, button.Action.IsTool
            ? Color.FromArgb(0x48, accent.R, accent.G, accent.B)
            : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        TextElement.SetForeground(button.Group, new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    }

    /// <summary>
    /// Fill and stroke share one solid colour: the thick round-joined stroke is what rounds the corners.
    /// Transparency goes on the element instead of the brush, so the overlapping fill and stroke are
    /// blended as one shape rather than doubling up into a lighter band.
    /// </summary>
    private static void Paint(Path shape, Color color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        shape.Fill = brush;
        shape.Stroke = brush;
        shape.Opacity = color.A / 255.0;
    }

    private void Rebuild()
    {
        int hot = _hot;
        _hot = -1;
        _layer.Children.Clear();
        _buttons.Clear();

        double outer = Radius - _settings.WheelSize * 0.022;
        double inner = CenterRadius + _settings.WheelSize * 0.022;
        double gap = Math.Max(3, _settings.WheelSize * 0.018);
        double corner = Math.Min(_settings.WheelSize * 0.03, (outer - inner) * 0.18);

        int n = _actions.Length;
        if (n == 0)
        {
            // Empty track until the files are known.
            _layer.Children.Add(new Path
            {
                Data = Ring(inner, outer),
                Fill = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            });
        }

        double step = n == 0 ? 360 : 360.0 / n;
        double labelRadius = (inner + outer) / 2;
        double arcLength = 2 * Math.PI * labelRadius / Math.Max(n, 1);
        double glyphSize = Math.Clamp(Math.Min(arcLength, outer - inner) * 0.26, 13, 24);
        double labelSize = Math.Clamp(Math.Min(arcLength, outer - inner) * 0.15, 9, 13);

        for (int i = 0; i < n; i++)
        {
            var action = _actions[i];
            double mid = i * step;

            var shape = new Path
            {
                Data = n == 1 ? Ring(inner + corner, outer - corner) : Segment(mid - step / 2, mid + step / 2, inner, outer, gap, corner),
                StrokeThickness = corner * 2,
                StrokeLineJoin = PenLineJoin.Round,
            };

            var content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = action.Glyph, FontFamily = IconFont, FontSize = glyphSize, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        Text = action.Label,
                        FontSize = labelSize,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 2, 0, 0),
                    },
                },
            };
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var at = PointAt(mid, labelRadius);
            SetLeft(content, at.X - content.DesiredSize.Width / 2);
            SetTop(content, at.Y - content.DesiredSize.Height / 2);

            var group = new Canvas { RenderTransform = new TranslateTransform(), Children = { shape, content } };
            var button = new WheelButton(group, shape, action, mid);
            ApplyRestingStyle(button);
            _layer.Children.Add(group);
            _buttons.Add(button);
        }

        _centerDisc = new Ellipse
        {
            Width = CenterRadius * 2,
            Height = CenterRadius * 2,
            Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
        };
        Highlight(CenterIndex, false);
        SetLeft(_centerDisc, Mid - CenterRadius);
        SetTop(_centerDisc, Mid - CenterRadius);
        _layer.Children.Add(_centerDisc);

        var center = new StackPanel
        {
            Width = CenterRadius * 2 - 12,
            Children =
            {
                new TextBlock
                {
                    Text = Glyphs.Cancel,
                    FontFamily = IconFont,
                    FontSize = Math.Max(11, CenterRadius * 0.32),
                    Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = _centerLabel,
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 26,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 3, 0, 0),
                },
            },
        };
        center.Measure(new Size(center.Width, double.PositiveInfinity));
        SetLeft(center, Mid - center.Width / 2);
        SetTop(center, Mid - center.DesiredSize.Height / 2);
        _layer.Children.Add(center);

        if (hot != -1 && (hot == CenterIndex || hot < n))
            SetHot(hot);
    }

    /// <summary>
    /// One ring segment between two angles, shrunk by <paramref name="corner"/> on every side. Stroking
    /// it with a round-joined pen of twice that width grows it back to full size with rounded corners.
    /// The sides are offset parallel to the slice's edges, so the gaps between segments are even.
    /// </summary>
    private Geometry Segment(double fromDeg, double toDeg, double inner, double outer, double gap, double corner)
    {
        double ro = outer - corner, ri = inner + corner;
        double offset = gap / 2 + corner;
        double outerInset = Math.Asin(Math.Min(1, offset / ro)) * 180 / Math.PI;
        double innerInset = Math.Asin(Math.Min(1, offset / ri)) * 180 / Math.PI;

        var figure = new PathFigure { StartPoint = PointAt(fromDeg + outerInset, ro), IsClosed = true };
        bool large = toDeg - fromDeg - 2 * outerInset > 180;
        figure.Segments.Add(new ArcSegment(PointAt(toDeg - outerInset, ro), new Size(ro, ro), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(PointAt(toDeg - innerInset, ri), true));
        figure.Segments.Add(new ArcSegment(PointAt(fromDeg + innerInset, ri), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true));
        return new PathGeometry(new[] { figure });
    }

    private Geometry Ring(double inner, double outer) => new GeometryGroup
    {
        FillRule = FillRule.EvenOdd,
        Children =
        {
            new EllipseGeometry(new Point(Mid, Mid), outer, outer),
            new EllipseGeometry(new Point(Mid, Mid), inner, inner),
        },
    };

    /// <summary>A point at an angle (degrees clockwise from 12 o'clock) and radius from the centre.</summary>
    private Point PointAt(double degrees, double radius)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(Mid + radius * Math.Sin(rad), Mid - radius * Math.Cos(rad));
    }

    /// <summary>Dark text on light accents, white text on dark ones.</summary>
    private static Color ContrastText(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.62 ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
}
