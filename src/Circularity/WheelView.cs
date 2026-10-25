using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Circularity;

/// <summary>
/// Draws the wheel: a translucent disc whose ring is split into curved, slightly rounded segments
/// (one per button, each with an icon and label) around a cancel button in the middle. Used by the
/// real wheel and by the live preview in Settings.
///
/// The ring holds at most six buttons so each stays big enough to aim at. When there are more, five
/// of them keep their place and the bottom slot becomes "More", which opens an arc of the rest just
/// outside the disc. Buttons that open a dialog (resize, crop, compress) only ever sit to the left
/// of "More": on the ring's left side, or at the left end of the arc.
///
/// The disc and the arc's backdrop are created once and never replaced. They are the only elements
/// that take part in hit-testing, and WPF tracks drag-and-drop by element: if they were rebuilt
/// mid-drag, every mouse move would look like a new "drag enter" and the drop would go to an element
/// that no longer exists. Everything else lives on layers that can be rebuilt freely. The arc is
/// collapsed (and so not hit-tested) until it opens, which is what lets a drag out of the disc
/// dismiss the wheel as usual.
/// </summary>
internal sealed class WheelView : Canvas
{
    /// <summary>Space around the wheel for its drop shadow.</summary>
    public const double ShadowMargin = 20;

    /// <summary><see cref="HitTest"/> result for the cancel button in the middle.</summary>
    public const int CenterIndex = -2;

    /// <summary><see cref="HitTest"/> result for the "More" button.</summary>
    public const int MoreIndex = -3;

    /// <summary>Most buttons the ring itself will ever show, "More" included.</summary>
    public const int MaxRingButtons = 6;

    /// <summary>
    /// The ring's slots, clockwise from the top. "More" takes the bottom one (or the top one when the
    /// arc opens upwards); the two on the left are for dialog buttons; the rest fill the others in order.
    /// </summary>
    private static readonly int[] LeftSlots = { 5, 4 };
    private static readonly int[] OtherSlotsArcBelow = { 0, 1, 2 };
    private static readonly int[] OtherSlotsArcAbove = { 1, 2, 3 };

    /// <summary>The widest the opened arc may be, so it never wraps back around the disc.</summary>
    private const double MaxExpansionSpan = 200;

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Duration HoverIn = TimeSpan.FromMilliseconds(130);
    private static readonly Duration HoverOut = TimeSpan.FromMilliseconds(180);
    private static readonly Duration IntroFade = TimeSpan.FromMilliseconds(120);
    private static readonly Duration IntroGrow = TimeSpan.FromMilliseconds(220);
    private static readonly Duration Outro = TimeSpan.FromMilliseconds(110);
    private static readonly Color CancelHot = Color.FromArgb(0x70, 0xE5, 0x48, 0x4D);

    /// <summary>How soft the shadow under the wheel is, and how far it drops.</summary>
    private const double ShadowBlur = 22, ShadowDepth = 4;

    private readonly Image _discShadow = new() { IsHitTestVisible = false };
    private readonly Image _arcShadow = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Canvas _backdrop = new();
    private readonly Ellipse _disc;
    private readonly Path _arc;
    private readonly Path _arcOutline = new() { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Canvas _layer = new() { IsHitTestVisible = false };
    private readonly Canvas _arcLayer = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<WheelButton> _buttons = new();
    private Settings _settings = new();
    private WheelAction[] _actions = Array.Empty<WheelAction>();
    private string _centerLabel = "";
    private Ellipse? _centerDisc;
    private SolidColorBrush? _centerFill;
    private TextBlock? _centerText;
    private int[] _ringTargets = Array.Empty<int>();
    private int[] _arcTargets = Array.Empty<int>();
    private double _arcStep, _arcSpan, _ringMid;
    private bool _expanded;
    private bool _arcAbove;
    private int _hot = -1;

    /// <summary>The animated parts of one button: its glow, its label colour, and where it points.</summary>
    private sealed record WheelButton(int Target, Canvas Group, Path Body, Path Glow, SolidColorBrush Text, double MidAngle, Point Center);

    /// <summary>Colours for one theme. Disc colours get their alpha from the opacity setting.</summary>
    private sealed record Palette(
        Color DiscCenter, Color DiscEdge, Color DiscStroke, Color Track, Color Segment, Color MoreOpen, Color Text,
        Color CenterFill, Color CenterStroke, Color CenterGlyph, Color CenterLabel, double ShadowOpacity);

    private static readonly Palette Dark = new(
        DiscCenter: Color.FromRgb(0x2C, 0x2C, 0x34), DiscEdge: Color.FromRgb(0x14, 0x14, 0x19),
        DiscStroke: Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Track: Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF),
        Segment: Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), MoreOpen: Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF),
        Text: Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF),
        CenterFill: Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), CenterStroke: Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
        CenterGlyph: Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), CenterLabel: Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF),
        ShadowOpacity: 0.45);

    private static readonly Palette Light = new(
        DiscCenter: Color.FromRgb(0xFF, 0xFF, 0xFF), DiscEdge: Color.FromRgb(0xEC, 0xEC, 0xF1),
        DiscStroke: Color.FromArgb(0x24, 0x00, 0x00, 0x00), Track: Color.FromArgb(0x0C, 0x00, 0x00, 0x00),
        Segment: Color.FromArgb(0x12, 0x00, 0x00, 0x00), MoreOpen: Color.FromArgb(0x20, 0x00, 0x00, 0x00),
        Text: Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1F),
        CenterFill: Color.FromArgb(0x0D, 0x00, 0x00, 0x00), CenterStroke: Color.FromArgb(0x1C, 0x00, 0x00, 0x00),
        CenterGlyph: Color.FromArgb(0xB0, 0x1A, 0x1A, 0x1F), CenterLabel: Color.FromArgb(0x99, 0x1A, 0x1A, 0x1F),
        ShadowOpacity: 0.22);

    private Palette _palette = Dark;

    public WheelView()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);

        _disc = new Ellipse { StrokeThickness = 1 };
        // The arc's backdrop doubles as its hit-test area, so it reaches back under the disc's edge
        // with no gap: moving out of "More" into the arc never leaves the wheel.
        _arc = new Path { Visibility = Visibility.Collapsed };

        _backdrop.Children.Add(_arc);
        _backdrop.Children.Add(_arcOutline);
        _backdrop.Children.Add(_disc);

        // The shadows are pictures drawn once (see Shadow), under the disc and the arc.
        Children.Add(_discShadow);
        Children.Add(_arcShadow);
        Children.Add(_backdrop);
        Children.Add(_layer);
        Children.Add(_arcLayer);
        ApplySettings(_settings);
    }

    /// <summary>Width and height of the whole view, including the opened arc and the drop shadow.</summary>
    public double Extent => _settings.WheelSize + 2 * (ShadowMargin + ArcRim - Radius);

    public IReadOnlyList<WheelAction> Actions => _actions;

    /// <summary>The highlighted button, <see cref="CenterIndex"/>, <see cref="MoreIndex"/>, or -1.</summary>
    public int HotIndex => _hot;

    /// <summary>Whether the "More" arc is open.</summary>
    public bool IsExpanded => _expanded;

    /// <summary>The ring's targets clockwise from the top, "More" included.</summary>
    public IReadOnlyList<int> RingOrder => _ringTargets;

    /// <summary>The arc's targets from its right end to its left end.</summary>
    public IReadOnlyList<int> ArcOrder => _arcTargets;

    /// <summary>
    /// Puts "More" at the top and opens its arc upwards, for when the wheel is too close to the bottom
    /// of the screen for the arc to fit below. Left and right stay the same way round.
    /// </summary>
    public bool ArcAbove
    {
        get => _arcAbove;
        set
        {
            if (value == _arcAbove)
                return;
            _arcAbove = value;
            Rebuild();
        }
    }

    /// <summary>How far the opened arc reaches from the wheel's centre, in device-independent pixels.</summary>
    public double ArcReach => ArcRim;

    /// <summary>Where the arc is centred: straight down, or straight up.</summary>
    private double ArcCenter => _arcAbove ? 0 : 180;

    /// <summary>+1 when going clockwise moves leftwards along the arc (below the wheel), -1 when it moves rightwards (above).</summary>
    private double ArcDirection => _arcAbove ? -1 : 1;

    private double Radius => _settings.WheelSize / 2.0;
    private double CenterRadius => _settings.WheelSize * 0.17;

    /// <summary>How far buttons sit inside the edge of whatever they're on, disc or arc.</summary>
    private double Inset => _settings.WheelSize * 0.022;
    private double Gap => Math.Max(3, _settings.WheelSize * 0.018);
    private double ArcInner => Radius + Inset;
    private double ArcOuter => ArcInner + _settings.WheelSize * 0.18;
    private double ArcRim => ArcOuter + Inset;
    private double Mid => Extent / 2;

    public void ApplySettings(Settings settings)
    {
        _settings = settings.Clone();
        _palette = Theme.IsDark(_settings.Theme) ? Dark : Light;
        Width = Height = _backdrop.Width = _backdrop.Height = _layer.Width = _layer.Height = _arcLayer.Width = _arcLayer.Height = Extent;

        // Same elements, new size and colour: see the class comment.
        byte alpha = (byte)Math.Round(_settings.Opacity * 255);
        _disc.Width = _disc.Height = Radius * 2;
        _disc.Fill = new RadialGradientBrush(WithAlpha(_palette.DiscCenter, alpha), WithAlpha(_palette.DiscEdge, alpha))
        {
            GradientOrigin = new Point(0.5, 0.35),
        };
        _disc.Stroke = new SolidColorBrush(_palette.DiscStroke);
        SetLeft(_disc, Mid - Radius);
        SetTop(_disc, Mid - Radius);
        _discShadow.Width = _discShadow.Height = _arcShadow.Width = _arcShadow.Height = Extent;
        _discShadow.Source = Shadow(new EllipseGeometry(new Point(Mid, Mid), Radius, Radius));

        // The very same gradient, laid out around the wheel's centre, so the arc is the disc's surface
        // carried on past its edge: same colour, same transparency.
        _arc.Fill = new RadialGradientBrush(WithAlpha(_palette.DiscCenter, alpha), WithAlpha(_palette.DiscEdge, alpha))
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(Mid, Mid),
            GradientOrigin = new Point(Mid, Mid - 0.3 * Radius),
            RadiusX = Radius,
            RadiusY = Radius,
        };
        _arcOutline.Stroke = new SolidColorBrush(_palette.DiscStroke);

        // The arc and its buttons grow outward from the wheel's centre, not their own.
        foreach (var element in ArcElements)
            element.RenderTransform = new ScaleTransform(1, 1, Mid, Mid);

        Rebuild();
    }

    private UIElement[] ArcElements => new UIElement[] { _arcShadow, _arc, _arcOutline, _arcLayer };

    /// <summary>
    /// The soft shadow under a shape, drawn once into a picture. A live blur effect would be redrawn
    /// on every frame of every animation, which (with the wheel drawn by the CPU, see App) costs more
    /// than everything else on the wheel put together. The shadow's strength follows the surface's
    /// transparency, as a real shadow through tinted glass would.
    /// </summary>
    private BitmapSource Shadow(Geometry shape)
    {
        int size = (int)Math.Ceiling(Extent);
        double strength = _settings.Opacity * _palette.ShadowOpacity;
        var path = new Path
        {
            Data = new GeometryGroup { Children = { shape }, Transform = new TranslateTransform(0, ShadowDepth) },
            Fill = new SolidColorBrush(Color.FromArgb((byte)Math.Round(strength * 255), 0, 0, 0)),
            Effect = new BlurEffect { Radius = ShadowBlur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality },
        };
        path.Measure(new Size(size, size));
        path.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(path);
        bitmap.Freeze();
        return bitmap;
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

    /// <summary>
    /// The target under a point in this view's coordinates: an index into <see cref="Actions"/>,
    /// <see cref="CenterIndex"/>, <see cref="MoreIndex"/>, or -1 for nothing.
    /// </summary>
    public int HitTest(Point p)
    {
        double dx = p.X - Mid, dy = p.Y - Mid;
        double r = Math.Sqrt(dx * dx + dy * dy);
        double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;

        if (r < CenterRadius)
            return CenterIndex;

        if (r <= Radius)
        {
            if (_ringTargets.Length == 0)
                return -1;
            // While the arc is open, the outer half of the disc in front of it keeps meaning "More",
            // so heading out to a button at one end doesn't clip a neighbouring segment and close it.
            if (_expanded && r >= _ringMid && InArcSpan(angle, r))
                return MoreIndex;
            // Each button owns its whole slice of the ring, gaps included.
            double step = 360.0 / _ringTargets.Length;
            return _ringTargets[(int)(((angle + step / 2) % 360) / step)];
        }

        if (_expanded && r <= ArcRim && InArcSpan(angle, r))
        {
            // Counted from the arc's right end.
            double along = ArcDirection * FromArcCenter(angle) + _arcSpan / 2;
            int index = Math.Clamp((int)Math.Floor(along / _arcStep), 0, _arcTargets.Length - 1);
            return _arcTargets[index];
        }
        return -1;
    }

    /// <summary>An angle relative to the arc's centre, from -180 to 180.</summary>
    private double FromArcCenter(double angle) => ((angle - ArcCenter) % 360 + 540) % 360 - 180;

    /// <summary>Whether an angle falls within the opened arc's backdrop, at a distance from the centre.</summary>
    private bool InArcSpan(double angle, double r)
    {
        if (_arcTargets.Length == 0)
            return false;
        double margin = Math.Max(0, Inset - Gap / 2) / Math.Max(r, 1) * 180 / Math.PI;
        return Math.Abs(FromArcCenter(angle)) <= _arcSpan / 2 + margin;
    }

    /// <summary>
    /// Highlights a target. Landing on "More" opens the arc and landing back on the ring closes it,
    /// so hovering, clicking and the arrow keys all behave the same way.
    /// </summary>
    public void SetHot(int target)
    {
        if (target == MoreIndex)
            Expand();
        else if (target == CenterIndex || _ringTargets.Contains(target))
            Collapse();
        Focus(target);
    }

    /// <summary>Closes the arc and puts the highlight back on "More", without opening it again.</summary>
    public void CollapseToMore()
    {
        Collapse();
        Focus(MoreIndex);
    }

    private void Focus(int target)
    {
        if (target == _hot)
            return;
        Highlight(_hot, false);
        _hot = target;
        Highlight(_hot, true);
    }

    /// <summary>Opens the arc of buttons that don't fit on the ring, the way the wheel itself appears.</summary>
    public void Expand()
    {
        if (_expanded || _arcTargets.Length == 0)
            return;
        _expanded = true;
        HighlightMore(true);
        var fadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var growEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        foreach (var element in ArcElements)
        {
            element.Visibility = Visibility.Visible;
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, IntroFade) { EasingFunction = fadeEase });
            var scale = (ScaleTransform)element.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, IntroGrow) { EasingFunction = growEase });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, IntroGrow) { EasingFunction = growEase });
        }
    }

    /// <summary>Closes the arc the way the wheel itself goes away: a quick fade and shrink.</summary>
    public void Collapse()
    {
        if (!_expanded)
            return;
        _expanded = false;
        HighlightMore(false);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        foreach (var element in ArcElements)
        {
            var fade = new DoubleAnimation(0, Outro) { EasingFunction = ease };
            if (element == _arcLayer)
            {
                fade.Completed += (_, _) =>
                {
                    // A reopen during the fade cancels the hide.
                    if (_expanded)
                        return;
                    foreach (var e in ArcElements)
                        e.Visibility = Visibility.Collapsed;
                };
            }
            element.BeginAnimation(OpacityProperty, fade);
            var scale = (ScaleTransform)element.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, Outro) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, Outro) { EasingFunction = ease });
        }
    }

    /// <summary>Fade in and grow with a slight overshoot when the wheel appears.</summary>
    public void PlayIntro()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, IntroFade) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        var grow = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        var scale = (ScaleTransform)RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, 1, IntroGrow) { EasingFunction = grow });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1, IntroGrow) { EasingFunction = grow });
    }

    /// <summary>A brief flash on a button, as feedback for a click that doesn't actually do anything (previews).</summary>
    public void Flash(int target)
    {
        if (target == MoreIndex)
            return;
        var flash = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        ButtonFor(target)?.Glow.BeginAnimation(OpacityProperty, flash);
    }

    /// <summary>Quick fade and shrink when the wheel goes away; <paramref name="done"/> runs at the end.</summary>
    public void PlayOutro(Action done)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, Outro) { EasingFunction = ease };
        fade.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, fade);
        var scale = (ScaleTransform)RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, Outro) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, Outro) { EasingFunction = ease });
    }

    private WheelButton? ButtonFor(int target) => _buttons.FirstOrDefault(b => b.Target == target);

    /// <summary>Where a button's icon and label sit, in this view's coordinates; null if it has no button.</summary>
    public Point? PointFor(int target) => ButtonFor(target)?.Center;

    /// <summary>The index in <see cref="Actions"/> that converts to <paramref name="target"/> (e.g. "pdf"), or -1.</summary>
    public int IndexOfTarget(string target) => Array.FindIndex(_actions, a => a.Target == target);

    /// <summary>
    /// Hover blends rather than snaps: the accent "glow" (the button grown out to the edge of the disc
    /// or arc it sits on, with square outer corners) fades in over the resting button, the label's
    /// colour crossfades, and the button slides out a little so it pops just past that edge. Leaving
    /// reverses it, a bit slower. "More" is the exception: see <see cref="HighlightMore"/>.
    /// </summary>
    private void Highlight(int target, bool on)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = on ? HoverIn : HoverOut;

        if (target == CenterIndex && _centerFill != null)
        {
            _centerFill.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(on ? CancelHot : _palette.CenterFill, duration) { EasingFunction = ease });
            // Say what letting go (or clicking) here does, rather than what the files are.
            if (_centerText != null)
                _centerText.Text = on ? "Cancel" : _centerLabel;
            return;
        }
        if (target == MoreIndex || ButtonFor(target) is not { } button)
            return;

        button.Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, duration) { EasingFunction = ease });
        button.Text.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(on ? ContrastText(_settings.Accent) : _palette.Text, duration) { EasingFunction = ease });

        double push = on ? _settings.WheelSize * 0.016 : 0;
        double rad = button.MidAngle * Math.PI / 180;
        var shift = (TranslateTransform)button.Group.RenderTransform;
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(push * Math.Sin(rad), duration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-push * Math.Cos(rad), duration) { EasingFunction = ease });
    }

    /// <summary>
    /// "More" lights up for as long as its arc is open, in a soft neutral tint rather than the accent:
    /// it fills out to the disc's edge where the arc begins, so it reads as the tab the arc hangs from,
    /// and the accent stays for the buttons that actually do something.
    /// </summary>
    private void HighlightMore(bool on)
    {
        if (ButtonFor(MoreIndex) is not { } more)
            return;
        // The tab is translucent, so the resting tile fades out under it rather than showing through
        // as a band of a different shade.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = on ? HoverIn : HoverOut;
        more.Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, duration) { EasingFunction = ease });
        more.Body.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0 : _palette.Segment.A / 255.0, duration) { EasingFunction = ease });
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
        _expanded = false;
        foreach (var element in ArcElements)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Visibility = Visibility.Collapsed;
        }
        _layer.Children.Clear();
        _arcLayer.Children.Clear();
        _buttons.Clear();

        double outer = Radius - Inset;
        double inner = CenterRadius + Inset;
        double corner = Math.Min(_settings.WheelSize * 0.03, (outer - inner) * 0.18);
        _ringMid = (inner + outer) / 2;

        Arrange();

        int ringCount = _ringTargets.Length;
        if (ringCount == 0)
        {
            // Empty track until the files are known.
            _layer.Children.Add(new Path { Data = Ring(inner, outer), Fill = new SolidColorBrush(_palette.Track) });
        }

        double step = ringCount == 0 ? 360 : 360.0 / ringCount;
        double arcLength = 2 * Math.PI * _ringMid / Math.Max(ringCount, 1);
        double glyphSize = Math.Clamp(Math.Min(arcLength, outer - inner) * 0.26, 13, 24);
        double labelSize = Math.Clamp(Math.Min(arcLength, outer - inner) * 0.15, 9, 13);

        for (int slot = 0; slot < ringCount; slot++)
        {
            int target = _ringTargets[slot];
            bool more = target == MoreIndex;
            var action = more ? new WheelAction(WheelActionKind.More, "More", Glyphs.More) : _actions[target];
            double mid = slot * step;
            var shape = ringCount == 1
                ? Ring(inner + corner, outer - corner)
                : Segment(mid - step / 2, mid + step / 2, inner, outer, Gap, corner);
            var glow = ringCount == 1
                ? Ring(inner, Radius)
                : Band(mid - step / 2, mid + step / 2, inner, Radius, Gap / 2, corner, 0);
            AddButton(_layer, target, action, shape, corner, glow, more ? _palette.MoreOpen : _settings.Accent,
                mid, _ringMid, glyphSize, labelSize);
        }

        // The arc's own buttons are shorter than the ring's, so their icons are a little smaller,
        // but the labels match.
        BuildArc(glyphSize * 0.8, labelSize);
        BuildCenter();

        if (hot != -1 && (hot == CenterIndex || _ringTargets.Contains(hot)))
            SetHot(hot);
    }

    /// <summary>
    /// Splits the buttons between the ring and the "More" arc. Without an overflow every button keeps
    /// its own slot in order. With one, "More" takes the bottom slot (or the top, see <see cref="ArcAbove"/>); the buttons that open a dialog
    /// take the two slots to its left, the rest fill the top and right; and whatever is left goes on
    /// the arc from its right end, with any dialog buttons last so they land at its left end.
    /// </summary>
    private void Arrange() => (_ringTargets, _arcTargets) = Layout(_actions, _arcAbove);

    /// <summary>
    /// Where each button goes, as indices into <paramref name="actions"/>: the ring's slots clockwise
    /// from the top (with <see cref="MoreIndex"/> for "More"), and the arc's from its right end.
    /// </summary>
    public static (int[] Ring, int[] Arc) Layout(IReadOnlyList<WheelAction> actions, bool arcAbove = false)
    {
        int n = actions.Count;
        if (n <= MaxRingButtons)
            return (Enumerable.Range(0, n).ToArray(), Array.Empty<int>());

        var dialogs = new Queue<int>(Enumerable.Range(0, n).Where(i => actions[i].OpensDialog));
        var others = new Queue<int>(Enumerable.Range(0, n).Where(i => !actions[i].OpensDialog));
        var ring = new int[MaxRingButtons];
        ring[arcAbove ? 0 : 3] = MoreIndex;
        foreach (int slot in arcAbove ? OtherSlotsArcAbove : OtherSlotsArcBelow)
            ring[slot] = others.Count > 0 ? others.Dequeue() : dialogs.Dequeue();
        foreach (int slot in LeftSlots)
            ring[slot] = dialogs.Count > 0 ? dialogs.Dequeue() : others.Dequeue();
        return (ring, others.Concat(dialogs).ToArray());
    }

    /// <summary>
    /// Lays the overflow buttons out along an arc centred under "More", just outside the disc. The
    /// backdrop frames them the way the disc frames the ring: the same margin on every side, and the
    /// same rounded corners, edge and surface.
    /// </summary>
    private void BuildArc(double glyphSize, double labelSize)
    {
        int count = _arcTargets.Length;
        if (count == 0)
        {
            _arc.Data = _arcOutline.Data = Geometry.Empty;
            _arcShadow.Source = null;
            return;
        }

        double labelRadius = (ArcInner + ArcOuter) / 2;
        // Each button is about as wide as it is tall, until the whole arc reaches its limit.
        double wanted = _settings.WheelSize * 0.24 / labelRadius * 180 / Math.PI;
        _arcStep = Math.Min(MaxExpansionSpan / count, wanted);
        _arcSpan = _arcStep * count;
        double arcFrom = ArcCenter - _arcSpan / 2, to = ArcCenter + _arcSpan / 2;

        double corner = Math.Min(_settings.WheelSize * 0.03, (ArcOuter - ArcInner) * 0.25);
        for (int i = 0; i < count; i++)
        {
            // i counts from the arc's right end, which is its clockwise start below the wheel and its
            // clockwise end above it.
            double mid = ArcCenter + ArcDirection * ((i + 0.5) * _arcStep - _arcSpan / 2);
            double from = mid - _arcStep / 2;
            bool atStart = Math.Abs(from - arcFrom) < 0.01, atEnd = Math.Abs(from + _arcStep - to) < 0.01;
            var shape = Segment(from, from + _arcStep, ArcInner, ArcOuter, Gap, corner);
            // Square outer corners like the ring's, except at the arc's two ends, where they follow
            // the backdrop's rounded corner instead of poking out past it.
            var glow = Band(from, from + _arcStep, ArcInner, ArcRim, Gap / 2, corner, 0,
                startOuterCorner: atStart ? corner : 0, endOuterCorner: atEnd ? corner : 0);
            AddButton(_arcLayer, _arcTargets[i], _actions[_arcTargets[i]], shape, corner, glow, _settings.Accent,
                mid, labelRadius, glyphSize, labelSize);
        }

        // The end buttons' sides sit half a gap inside the arc's span, so the backdrop's ends sit the
        // margin beyond those. Its inner edge tucks just under the disc's, leaving no seam to cross.
        double side = Gap / 2 - Inset;
        double outerCorner = Math.Min(corner + Inset, (ArcRim - Radius) / 2);
        _arc.Data = Band(arcFrom, to, Radius - 1, ArcRim, side, 0, outerCorner);
        // Drawn from the part outside the disc only, so the two shadows meet without doubling up.
        _arcShadow.Source = Shadow(Band(arcFrom, to, Radius, ArcRim, side, 0, outerCorner));
        // The outline follows the backdrop's sides and outer edge; along the disc, the disc's own edge does.
        _arcOutline.Data = Band(arcFrom, to, Radius, ArcRim, side, 0, outerCorner, closed: false);
    }

    private void BuildCenter()
    {
        _centerFill = new SolidColorBrush(_palette.CenterFill);
        _centerDisc = new Ellipse
        {
            Width = CenterRadius * 2,
            Height = CenterRadius * 2,
            Fill = _centerFill,
            Stroke = new SolidColorBrush(_palette.CenterStroke),
            StrokeThickness = 1,
        };
        SetLeft(_centerDisc, Mid - CenterRadius);
        SetTop(_centerDisc, Mid - CenterRadius);
        _layer.Children.Add(_centerDisc);

        _centerText = new TextBlock
        {
            Text = _centerLabel,
            FontSize = 9.5,
            Foreground = new SolidColorBrush(_palette.CenterLabel),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 26,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        };
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
                _centerText,
            },
        };
        center.Measure(new Size(center.Width, double.PositiveInfinity));
        SetLeft(center, Mid - center.Width / 2);
        // Placed once, so swapping in "Cancel" for a two-line label doesn't make the icon jump.
        SetTop(center, Mid - center.DesiredSize.Height / 2);
        _layer.Children.Add(center);
    }

    /// <param name="corner">Rounding of the resting shape, which <see cref="Segment"/> left for the stroke to add.</param>
    private void AddButton(Canvas host, int target, WheelAction action, Geometry shape, double corner,
                           Geometry glowShape, Color glowColor, double midAngle, double labelRadius,
                           double glyphSize, double labelSize)
    {
        var body = new Path { Data = shape, StrokeThickness = corner * 2, StrokeLineJoin = PenLineJoin.Round };
        // Tools are tinted with the accent so they read as a different kind of button.
        Paint(body, action.IsTool ? WithAlpha(_settings.Accent, 0x48) : _palette.Segment);

        // The glow is a single filled shape, so its transparency can live in the brush and its
        // opacity is free for the fade.
        var glow = new Path { Data = glowShape, Fill = new SolidColorBrush(glowColor), Opacity = 0 };

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
        var at = PointAt(midAngle, labelRadius);
        SetLeft(content, at.X - content.DesiredSize.Width / 2);
        SetTop(content, at.Y - content.DesiredSize.Height / 2);

        var text = new SolidColorBrush(_palette.Text);
        var group = new Canvas { RenderTransform = new TranslateTransform(), Children = { body, glow, content } };        TextElement.SetForeground(group, text);
        host.Children.Add(group);
        _buttons.Add(new WheelButton(target, group, body, glow, text, midAngle, at));
    }

    /// <summary>
    /// One ring segment between two angles, shrunk by <paramref name="corner"/> on every side. Stroking
    /// it with a round-joined pen of twice that width grows it back to full size with rounded corners.
    /// The sides are offset parallel to the slice's edges, so the gaps between segments are even.
    /// </summary>
    private PathGeometry Segment(double fromDeg, double toDeg, double inner, double outer, double gap, double corner)
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
    /// A band between two angles and radii whose corners can each be square (0) or rounded. It's what
    /// the hover glows are (rounded inner corners, square outer ones, reaching the edge the button sits
    /// on) and what the arc's backdrop is (the reverse). Corners are true fillet arcs: a circle of the
    /// corner's radius touching both the side and the edge, since the stroke trick in
    /// <see cref="Segment"/> would round every corner alike.
    /// </summary>
    /// <param name="side">How far each side sits inside its radial line; negative puts it outside.</param>
    /// <param name="closed">False leaves off the inner edge, for an outline that stops at the disc.</param>
    /// <param name="startOuterCorner">Overrides <paramref name="outerCorner"/> on the start side only.</param>
    /// <param name="endOuterCorner">Overrides <paramref name="outerCorner"/> on the end side only.</param>
    private PathGeometry Band(double fromDeg, double toDeg, double inner, double outer, double side,
                              double innerCorner, double outerCorner, bool closed = true,
                              double? startOuterCorner = null, double? endOuterCorner = null)
    {
        double startCorner = startOuterCorner ?? outerCorner, endCorner = endOuterCorner ?? outerCorner;
        // Point "along" the radial line at an angle, then "across" it towards the band's inside.
        Point At(double degrees, bool isStart, double along, double across)
        {
            double rad = degrees * Math.PI / 180;
            double ux = Math.Sin(rad), uy = -Math.Cos(rad);              // outward along the edge
            double sign = isStart ? 1 : -1;                              // inside is clockwise of the start edge
            double nx = Math.Cos(rad) * sign, ny = Math.Sin(rad) * sign; // across, towards the inside
            return new Point(Mid + along * ux + across * nx, Mid + along * uy + across * ny);
        }

        // Where a corner's fillet touches the side, and where it touches the curved edge.
        (Point Side, Point Edge) Corner(double degrees, bool isStart, double radius, double corner, bool outerEdge)
        {
            if (corner <= 0)
            {
                var p = At(degrees, isStart, Math.Sqrt(radius * radius - side * side), side);
                return (p, p);
            }
            double toCentre = outerEdge ? radius - corner : radius + corner;
            double along = Math.Sqrt(toCentre * toCentre - (side + corner) * (side + corner));
            var centre = At(degrees, isStart, along, side + corner);
            double scale = radius / toCentre;
            return (At(degrees, isStart, along, side), new Point(Mid + (centre.X - Mid) * scale, Mid + (centre.Y - Mid) * scale));
        }

        var startOut = Corner(fromDeg, true, outer, startCorner, true);
        var endOut = Corner(toDeg, false, outer, endCorner, true);
        var endIn = Corner(toDeg, false, inner, innerCorner, false);
        var startIn = Corner(fromDeg, true, inner, innerCorner, false);
        bool large = toDeg - fromDeg > 180;

        void Fillet(PathFigure f, Point to, double corner)
        {
            if (corner > 0)
                f.Segments.Add(new ArcSegment(to, new Size(corner, corner), 0, false, SweepDirection.Clockwise, true));
        }

        // Clockwise round the outline: out along the start side, along the outer edge, back in along
        // the end side, then back along the inner edge.
        var figure = new PathFigure { StartPoint = startIn.Side, IsClosed = closed };
        figure.Segments.Add(new LineSegment(startOut.Side, true));
        Fillet(figure, startOut.Edge, startCorner);
        figure.Segments.Add(new ArcSegment(endOut.Edge, new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        Fillet(figure, endOut.Side, endCorner);
        figure.Segments.Add(new LineSegment(endIn.Side, true));
        if (closed)
        {
            Fillet(figure, endIn.Edge, innerCorner);
            figure.Segments.Add(new ArcSegment(startIn.Edge, new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
            Fillet(figure, startIn.Side, innerCorner);
        }
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
