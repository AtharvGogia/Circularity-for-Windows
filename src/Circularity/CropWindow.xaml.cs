using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Circularity.Engine;

namespace Circularity;

internal enum CropOutcome { Cancelled, Skipped, Cropped }

/// <summary>
/// Shows an image (or a frame from a video) with a crop box that can be moved, resized from its
/// corners and edges, redrawn, and locked to an aspect ratio. Works in source pixels internally.
/// </summary>
internal partial class CropWindow : Window
{
    private static readonly (string Label, double Ratio)[] Ratios =
    {
        ("Free", 0), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    };

    [Flags]
    private enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private const double HandleSize = 10;
    private const double GrabDistance = 9;
    private const double MinSize = 8;

    private readonly int _imageWidth, _imageHeight;
    private readonly double _scale; // Display units per image pixel.
    private readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly List<Line> _thirds = new();
    private readonly List<Rectangle> _handles = new();

    private Rect _crop; // In image pixels.
    private double _ratio;
    private Edges _dragging;
    private Point _dragStart;
    private Rect _cropAtDragStart;

    public CropOutcome Outcome { get; private set; } = CropOutcome.Cancelled;

    public PixelRect Result => new(
        (int)Math.Round(_crop.X), (int)Math.Round(_crop.Y),
        Math.Max(1, (int)Math.Round(_crop.Width)), Math.Max(1, (int)Math.Round(_crop.Height)));

    public CropWindow(BitmapSource image, string fileName, int index, int count)
    {
        InitializeComponent();
        Title = count > 1 ? $"Crop {fileName} ({index} of {count})" : $"Crop {fileName}";
        SkipButton.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;

        _imageWidth = image.PixelWidth;
        _imageHeight = image.PixelHeight;

        // Fit the picture in most of the screen; small images may be shown up to 2x.
        var work = SystemParameters.WorkArea;
        _scale = Math.Min(2, Math.Min(work.Width * 0.8 / _imageWidth, (work.Height * 0.8 - 120) / _imageHeight));
        double displayWidth = _imageWidth * _scale, displayHeight = _imageHeight * _scale;

        Picture.Source = image;
        Picture.Width = Overlay.Width = displayWidth;
        Picture.Height = Overlay.Height = displayHeight;
        RenderOptions.SetBitmapScalingMode(Picture, BitmapScalingMode.HighQuality);

        Overlay.Children.Add(_shade);
        for (int i = 0; i < 4; i++)
        {
            var line = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, IsHitTestVisible = false };
            _thirds.Add(line);
            Overlay.Children.Add(line);
        }
        Overlay.Children.Add(_frame);
        for (int i = 0; i < 8; i++)
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                RadiusX = 2,
                RadiusY = 2,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            _handles.Add(handle);
            Overlay.Children.Add(handle);
        }

        foreach (var (label, ratio) in Ratios)
        {
            var option = new RadioButton { Content = label, GroupName = "Ratio", Margin = new Thickness(0, 0, 16, 0), IsChecked = ratio == 0 };
            option.Checked += (_, _) => SetRatio(ratio);
            RatioPanel.Children.Add(option);
        }

        Overlay.MouseLeftButtonDown += OnMouseDown;
        Overlay.MouseMove += OnMouseMove;
        Overlay.MouseLeftButtonUp += (_, _) =>
        {
            _dragging = Edges.None;
            Overlay.ReleaseMouseCapture();
        };

        _crop = new Rect(0, 0, _imageWidth, _imageHeight);
        Loaded += (_, _) => WindowHelpers.ForceForeground(this);
        Redraw();
    }

    private void SetRatio(double ratio)
    {
        _ratio = ratio;
        if (ratio > 0)
        {
            // The largest box of that shape, centred on the picture.
            double w = _imageWidth, h = w / ratio;
            if (h > _imageHeight)
            {
                h = _imageHeight;
                w = h * ratio;
            }
            _crop = new Rect((_imageWidth - w) / 2, (_imageHeight - h) / 2, w, h);
        }
        Redraw();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        _dragging = EdgesAt(p);
        if (_dragging == Edges.None)
        {
            // Start a new box anchored where the user pressed; dragging moves its bottom-right corner.
            var anchor = ToImage(p);
            _crop = new Rect(anchor, anchor);
            _dragging = Edges.Right | Edges.Bottom;
        }
        _dragStart = ToImage(p);
        _cropAtDragStart = _crop;
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (_dragging == Edges.None)
        {
            Overlay.Cursor = CursorFor(EdgesAt(p));
            return;
        }

        var q = ToImage(p);
        _crop = _dragging == Edges.Move ? Moved(q) : Resized(q);
        Redraw();
    }

    private Rect Moved(Point q)
    {
        var r = _cropAtDragStart;
        double x = Math.Clamp(r.X + q.X - _dragStart.X, 0, _imageWidth - r.Width);
        double y = Math.Clamp(r.Y + q.Y - _dragStart.Y, 0, _imageHeight - r.Height);
        return new Rect(x, y, r.Width, r.Height);
    }

    private Rect Resized(Point q)
    {
        var r = _cropAtDragStart;
        bool horizontal = _dragging.HasFlag(Edges.Left) || _dragging.HasFlag(Edges.Right);
        bool vertical = _dragging.HasFlag(Edges.Top) || _dragging.HasFlag(Edges.Bottom);

        if (horizontal && vertical)
        {
            // Corner: the opposite corner stays put.
            var anchor = new Point(_dragging.HasFlag(Edges.Left) ? r.Right : r.Left, _dragging.HasFlag(Edges.Top) ? r.Bottom : r.Top);
            return FromAnchor(anchor, q);
        }

        if (_ratio == 0)
        {
            double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
            if (_dragging.HasFlag(Edges.Left)) left = Math.Min(q.X, right - MinSize);
            if (_dragging.HasFlag(Edges.Right)) right = Math.Max(q.X, left + MinSize);
            if (_dragging.HasFlag(Edges.Top)) top = Math.Min(q.Y, bottom - MinSize);
            if (_dragging.HasFlag(Edges.Bottom)) bottom = Math.Max(q.Y, top + MinSize);
            return new Rect(new Point(left, top), new Point(right, bottom));
        }

        // Single edge with a locked ratio: that edge follows the cursor, the other axis grows
        // around the box's centre line.
        if (horizontal)
        {
            double fixedX = _dragging.HasFlag(Edges.Left) ? r.Right : r.Left;
            double cy = r.Top + r.Height / 2;
            double w = Math.Max(MinSize, Math.Abs(q.X - fixedX));
            w = Math.Min(w, _dragging.HasFlag(Edges.Left) ? fixedX : _imageWidth - fixedX);
            double h = w / _ratio;
            double maxH = 2 * Math.Min(cy, _imageHeight - cy);
            if (h > maxH)
            {
                h = maxH;
                w = h * _ratio;
            }
            double x = _dragging.HasFlag(Edges.Left) ? fixedX - w : fixedX;
            return new Rect(x, cy - h / 2, w, h);
        }
        else
        {
            double fixedY = _dragging.HasFlag(Edges.Top) ? r.Bottom : r.Top;
            double cx = r.Left + r.Width / 2;
            double h = Math.Max(MinSize, Math.Abs(q.Y - fixedY));
            h = Math.Min(h, _dragging.HasFlag(Edges.Top) ? fixedY : _imageHeight - fixedY);
            double w = h * _ratio;
            double maxW = 2 * Math.Min(cx, _imageWidth - cx);
            if (w > maxW)
            {
                w = maxW;
                h = w / _ratio;
            }
            double y = _dragging.HasFlag(Edges.Top) ? fixedY - h : fixedY;
            return new Rect(cx - w / 2, y, w, h);
        }
    }

    /// <summary>Box from a fixed corner to the cursor, honouring the ratio and the picture's bounds.</summary>
    private Rect FromAnchor(Point anchor, Point q)
    {
        int dirX = q.X >= anchor.X ? 1 : -1;
        int dirY = q.Y >= anchor.Y ? 1 : -1;
        double maxW = dirX > 0 ? _imageWidth - anchor.X : anchor.X;
        double maxH = dirY > 0 ? _imageHeight - anchor.Y : anchor.Y;
        double w = Math.Min(Math.Max(MinSize, Math.Abs(q.X - anchor.X)), maxW);
        double h = Math.Min(Math.Max(MinSize, Math.Abs(q.Y - anchor.Y)), maxH);

        if (_ratio > 0)
        {
            if (w / h > _ratio)
                h = w / _ratio;
            else
                w = h * _ratio;
            if (w > maxW)
            {
                w = maxW;
                h = w / _ratio;
            }
            if (h > maxH)
            {
                h = maxH;
                w = h * _ratio;
            }
        }

        double x = dirX > 0 ? anchor.X : anchor.X - w;
        double y = dirY > 0 ? anchor.Y : anchor.Y - h;
        return new Rect(x, y, w, h);
    }

    private Edges EdgesAt(Point p)
    {
        var d = ToDisplay(_crop);
        bool nearLeft = Math.Abs(p.X - d.Left) <= GrabDistance, nearRight = Math.Abs(p.X - d.Right) <= GrabDistance;
        bool nearTop = Math.Abs(p.Y - d.Top) <= GrabDistance, nearBottom = Math.Abs(p.Y - d.Bottom) <= GrabDistance;
        bool withinX = p.X >= d.Left - GrabDistance && p.X <= d.Right + GrabDistance;
        bool withinY = p.Y >= d.Top - GrabDistance && p.Y <= d.Bottom + GrabDistance;

        var edges = Edges.None;
        if (withinY && nearLeft) edges |= Edges.Left;
        else if (withinY && nearRight) edges |= Edges.Right;
        if (withinX && nearTop) edges |= Edges.Top;
        else if (withinX && nearBottom) edges |= Edges.Bottom;

        if (edges == Edges.None && d.Contains(p))
            edges = Edges.Move;
        return edges;
    }

    private static Cursor CursorFor(Edges edges) => edges switch
    {
        Edges.Move => Cursors.SizeAll,
        Edges.Left | Edges.Top or Edges.Right | Edges.Bottom => Cursors.SizeNWSE,
        Edges.Right | Edges.Top or Edges.Left | Edges.Bottom => Cursors.SizeNESW,
        Edges.Left or Edges.Right => Cursors.SizeWE,
        Edges.Top or Edges.Bottom => Cursors.SizeNS,
        _ => Cursors.Cross,
    };

    private Point ToImage(Point display) => new(
        Math.Clamp(display.X / _scale, 0, _imageWidth),
        Math.Clamp(display.Y / _scale, 0, _imageHeight));

    private Rect ToDisplay(Rect r) => new(r.X * _scale, r.Y * _scale, r.Width * _scale, r.Height * _scale);

    private void Redraw()
    {
        var d = ToDisplay(_crop);
        _shade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, Overlay.Width, Overlay.Height)), new RectangleGeometry(d) },
        };

        _frame.Width = d.Width;
        _frame.Height = d.Height;
        Canvas.SetLeft(_frame, d.X);
        Canvas.SetTop(_frame, d.Y);

        // Rule-of-thirds guides.
        for (int i = 0; i < 2; i++)
        {
            double x = d.X + d.Width * (i + 1) / 3, y = d.Y + d.Height * (i + 1) / 3;
            SetLine(_thirds[i], x, d.Top, x, d.Bottom);
            SetLine(_thirds[i + 2], d.Left, y, d.Right, y);
        }

        var points = new[]
        {
            d.TopLeft, new Point(d.X + d.Width / 2, d.Top), d.TopRight, new Point(d.Right, d.Y + d.Height / 2),
            d.BottomRight, new Point(d.X + d.Width / 2, d.Bottom), d.BottomLeft, new Point(d.Left, d.Y + d.Height / 2),
        };
        for (int i = 0; i < points.Length; i++)
        {
            Canvas.SetLeft(_handles[i], points[i].X - HandleSize / 2);
            Canvas.SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }

        var result = Result;
        SizeText.Text = $"{result.Width} × {result.Height} px";
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1;
        line.Y1 = y1;
        line.X2 = x2;
        line.Y2 = y2;
    }

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        Outcome = CropOutcome.Cropped;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Outcome = CropOutcome.Skipped;
        DialogResult = false;
    }
}
