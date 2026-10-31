using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Circularity;

/// <summary>
/// A button that shows a color swatch and opens the Windows color dialog when clicked.
/// </summary>
internal sealed class ColorPickerButton : Button
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(Color), typeof(ColorPickerButton),
            new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));

    /// <summary>Routed event raised when the color changes.</summary>
    public static readonly RoutedEvent ColorChangedEvent =
        EventManager.RegisterRoutedEvent(nameof(ColorChanged), RoutingStrategy.Bubble,
            typeof(RoutedPropertyChangedEventHandler<Color>), typeof(ColorPickerButton));

    /// <summary>Event raised when the color changes.</summary>
    public event RoutedPropertyChangedEventHandler<Color> ColorChanged
    {
        add => AddHandler(ColorChangedEvent, value);
        remove => RemoveHandler(ColorChangedEvent, value);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private readonly Ellipse _swatch;

    public ColorPickerButton()
    {
        Padding = new Thickness(2);
        MinWidth = 40;
        MinHeight = 28;
        Click += OnClick;

        _swatch = new Ellipse
        {
            Width = 20,
            Height = 20,
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color)
        };
        Content = _swatch;
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorPickerButton btn)
        {
            btn._swatch.Fill = new SolidColorBrush((Color)e.NewValue);
            btn.RaiseEvent(new RoutedPropertyChangedEventArgs<Color>((Color)e.OldValue, (Color)e.NewValue, ColorPickerButton.ColorChangedEvent));
        }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorDialog { Color = Color };
        if (dialog.ShowDialog() == true)
            Color = dialog.Color;
    }
}

/// <summary>
/// Wrapper around the native Windows color dialog (ChooseColor).
/// </summary>
internal sealed class ColorDialog
{
    private const int CC_RGBINIT = 0x00000001;
    private const int CC_FULLOPEN = 0x00000002;
    private const int CC_ENABLEHOOK = 0x00000010;
    private const int CC_ENABLETEMPLATE = 0x00000020;
    private const int CC_SOLIDCOLOR = 0x00000080;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
    }

    [System.Runtime.InteropServices.DllImport("comdlg32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern bool ChooseColor(ref CHOOSECOLOR pcc);

    private readonly int[] _custColors = new int[16];

    public Color Color { get; set; } = Colors.White;

    public bool? ShowDialog()
    {
        var cc = new CHOOSECOLOR
        {
            lStructSize = System.Runtime.InteropServices.Marshal.SizeOf<CHOOSECOLOR>(),
            hwndOwner = IntPtr.Zero,
            rgbResult = Color.R | (Color.G << 8) | (Color.B << 16),
            lpCustColors = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(_custColors, 0),
            Flags = CC_RGBINIT | CC_FULLOPEN | CC_SOLIDCOLOR
        };

        // Pin the custom colors array
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(_custColors, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            cc.lpCustColors = handle.AddrOfPinnedObject();
            if (ChooseColor(ref cc))
            {
                int rgb = cc.rgbResult;
                Color = Color.FromRgb((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));
                return true;
            }
            return false;
        }
        finally
        {
            handle.Free();
        }
    }
}