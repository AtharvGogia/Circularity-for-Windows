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

namespace Circularity;

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

    /// <summary>The drawn parts of one button, and its resting and hovered outlines, so hover can swap them.</summary>
    private sealed record WheelButton(
        Canvas Group, Path Shape, WheelAction Action, Geometry RestingShape, Geometry HoveredShape, double CornerStroke, double MidAngle);

    /// <summary>Colours for one theme. Disc colours get their alpha from the opacity setting.</summary>
    private sealed record Palette(
        Color DiscCenter, Color DiscEdge, Color DiscStroke, Color Track, Color Segment, Color Text,
        Color CenterFill, Color CenterStroke, Color CenterGlyph, Color CenterLabel, double ShadowOpacity);

    private static readonly Palette Dark = new(
        DiscCenter: Color.FromRgb(0x2C, 0x2C, 0x34), DiscEdge: Color.FromRgb(0x14, 0x14, 0x19),
        DiscStroke: Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Track: Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
        Segment: Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), Text: Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF),
        CenterFill: Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), CenterStroke: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
        CenterGlyph: Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), CenterLabel: Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF),
        ShadowOpacity: 0.45);

    private static readonly Palette Light = new(
        DiscCenter: Color.FromRgb(0xFF, 0xFF, 0xFF), DiscEdge: Color.FromRgb(0xEC, 0xEC, 0xF1),
        DiscStroke: Color.FromArgb(0x24, 0x00, 0x00, 0x00), Track: Color.FromArgb(0x0C, 0x00, 0x00, 0x00),
        Segment: Color.FromArgb(0x12, 0x00, 0x00, 0x00), Text: Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1F),
        CenterFill: Color.FromArgb(0x0D, 0x00, 0x00, 0x00), CenterStroke: Color.FromArgb(0x1C, 0x00, 0x00, 0x00),
        CenterGlyph: Color.FromArgb(0xB0, 0x1A, 0x1A, 0x1F), CenterLabel: Color.FromArgb(0x99, 0x1A, 0x1A, 0x1F),
        ShadowOpacity: 0.22);

    private Palette _palette = Dark;

    public WheelView()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);

        _disc = new Ellipse
        {
            StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Color = Colors.Black },
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
        _palette = Theme.IsDark(_settings.Theme) ? Dark : Light;
        Width = Height = _layer.Width = _layer.Height = Extent;

        // Same element, new size and colour: see the class comment.
        byte alpha = (byte)Math.Round(_settings.Opacity * 255);
        _disc.Width = _disc.Height = Radius * 2;
        _disc.Fill = new RadialGradientBrush(WithAlpha(_palette.DiscCenter, alpha), WithAlpha(_palette.DiscEdge, alpha))
        {
            GradientOrigin = new Point(0.5, 0.35),
        };
        _disc.Stroke = new SolidColorBrush(_palette.DiscStroke);
        ((DropShadowEffect)_disc.Effect).Opacity = _palette.ShadowOpacity;
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
            _centerDisc.Fill = new SolidColorBrush(on ? Color.FromArgb(0x70, 0xE5, 0x48, 0x4D) : _palette.CenterFill);
            return;
        }
        if (index < 0 || index >= _buttons.Count)
            return;

        var button = _buttons[index];
        if (on)
        {
            // The hovered segment grows out to the rim: its outer edge follows the disc's own edge,
            // with square outer corners, while the inner corners stay rounded.
            var accent = _settings.Accent;
            button.Shape.Data = button.HoveredShape;
            button.Shape.StrokeThickness = 0;
            Paint(button.Shape, accent);
            TextElement.SetForeground(button.Group, new SolidColorBrush(ContrastText(accent)));
        }
        else
        {
            button.Shape.Data = button.RestingShape;
            button.Shape.StrokeThickness = button.CornerStroke;
            ApplyRestingStyle(button);
        }

        // ...and slides out a little, so it pops just past the disc's edge.
        double push = on ? _settings.WheelSize * 0.016 : 0;
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
        Paint(button.Shape, button.Action.IsTool ? WithAlpha(accent, 0x48) : _palette.Segment);
        TextElement.SetForeground(button.Group, new SolidColorBrush(_palette.Text));
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
                Fill = new SolidColorBrush(_palette.Track),
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

            var resting = n == 1 ? Ring(inner + corner, outer - corner) : Segment(mid - step / 2, mid + step / 2, inner, outer, gap, corner);
            var hovered = n == 1 ? Ring(inner, Radius) : RimSegment(mid - step / 2, mid + step / 2, inner, gap, corner);
            resting.Freeze();
            hovered.Freeze();
            var shape = new Path
            {
                Data = resting,
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
            var button = new WheelButton(group, shape, action, resting, hovered, corner * 2, mid);
            ApplyRestingStyle(button);
            _layer.Children.Add(group);
            _buttons.Add(button);
        }

        _centerDisc = new Ellipse
        {
            Width = CenterRadius * 2,
            Height = CenterRadius * 2,
            Stroke = new SolidColorBrush(_palette.CenterStroke),
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
                    Foreground = new SolidColorBrush(_palette.CenterGlyph),
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = _centerLabel,
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(_palette.CenterLabel),
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

    /// <summary>
    /// The hovered form of a segment: same inner edge, sides and gaps, but its outer edge is the disc's
    /// own edge, with square outer corners. The inner corners are rounded with true fillet arcs (a
    /// circle of radius <paramref name="corner"/> touching both the side and the inner edge), since the
    /// stroke trick in <see cref="Segment"/> would round every corner.
    /// </summary>
    private Geometry RimSegment(double fromDeg, double toDeg, double inner, double gap, double corner)
    {
        double side = gap / 2; // Each side sits this far inside the slice's radial edge.
        double rim = Radius;

        // Point "along" the radial line at an angle, then "across" it towards the segment's inside.
        Point At(double degrees, bool isStart, double along, double across)
        {
            double rad = degrees * Math.PI / 180;
            double ux = Math.Sin(rad), uy = -Math.Cos(rad);              // outward along the edge
            double sign = isStart ? 1 : -1;                              // inside is clockwise of the start edge
            double nx = Math.Cos(rad) * sign, ny = Math.Sin(rad) * sign; // across, towards the inside
            return new Point(Mid + along * ux + across * nx, Mid + along * uy + across * ny);
        }

        // Where a side meets the rim, and where each inner fillet touches the side and the inner edge.
        double outerAlong = Math.Sqrt(rim * rim - side * side);
        double filletAlong = Math.Sqrt((inner + corner) * (inner + corner) - (side + corner) * (side + corner));
        Point InnerTouch(double degrees, bool isStart)
        {
            var c = At(degrees, isStart, filletAlong, side + corner); // fillet centre
            double scale = inner / (inner + corner);
            return new Point(Mid + (c.X - Mid) * scale, Mid + (c.Y - Mid) * scale);
        }

        var startOuter = At(fromDeg, true, outerAlong, side);
        var endOuter = At(toDeg, false, outerAlong, side);
        var endSide = At(toDeg, false, filletAlong, side);
        var endInner = InnerTouch(toDeg, false);
        var startInner = InnerTouch(fromDeg, true);
        var startSide = At(fromDeg, true, filletAlong, side);
        bool large = toDeg - fromDeg > 180;

        var figure = new PathFigure { StartPoint = startOuter, IsClosed = true };
        figure.Segments.Add(new ArcSegment(endOuter, new Size(rim, rim), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(endSide, true));
        figure.Segments.Add(new ArcSegment(endInner, new Size(corner, corner), 0, false, SweepDirection.Clockwise, true));
        figure.Segments.Add(new ArcSegment(startInner, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new ArcSegment(startSide, new Size(corner, corner), 0, false, SweepDirection.Clockwise, true));
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

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Dark text on light accents, white text on dark ones.</summary>
    private static Color ContrastText(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.62 ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
}
