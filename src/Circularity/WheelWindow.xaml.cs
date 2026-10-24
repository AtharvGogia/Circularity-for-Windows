using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Circularity;

/// <summary>
/// The transparent, topmost window that hosts the wheel. It works two ways:
///
///  - Drag mode (the gesture): it never takes focus and is a real OLE drop target. The user keeps
///    dragging their file(s) onto a button and releases, and the files arrive through a normal drop.
///    Dragging off the wheel dismisses it and lets the drag carry on as if nothing happened.
///  - Click mode (hotkey, Explorer menu, Send to): the files are already known. Hover and click a
///    button, or use the arrow keys and Enter; Esc, the centre, or clicking elsewhere cancels.
///
/// Either way the centre is a cancel button, which is where the cursor starts.
/// </summary>
internal partial class WheelWindow : Window
{
    /// <summary>Raised when a button is chosen, by drop or by click/keyboard.</summary>
    public event Action<string[], WheelAction>? ActionChosen;

    /// <summary>Whether an installed converter can apply an operation to a file; decides which buttons appear.</summary>
    public Func<string, Engine.Operation, bool> CanHandle { get; set; } = (_, _) => true;

    private readonly WheelView _view = new();
    private readonly DispatcherTimer _dropTimer;
    private readonly DispatcherTimer _leaveTimer;
    private bool _dropped;
    private bool _clickMode;
    private string[] _clickFiles = Array.Empty<string>();
    private IntPtr _hwnd;

    public WheelWindow()
    {
        InitializeComponent();
        Content = _view;

        // The hook sees the mouse button come up before OLE delivers the drop to us,
        // so wait briefly for the drop before hiding.
        _dropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dropTimer.Tick += (_, _) =>
        {
            _dropTimer.Stop();
            if (!_dropped)
            {
                Log.Write("Drag ended without a drop on the wheel.");
                Hide();
            }
        };

        // Leaving the wheel mid-drag dismisses it. The short delay absorbs the leave/enter
        // pair OLE can send as the wheel appears under the cursor.
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            Log.Write("Dragged off the wheel; dismissed it.");
            Hide();
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };

        // In click mode, clicking anywhere else (or switching apps) cancels.
        Deactivated += (_, _) =>
        {
            if (_clickMode && IsVisible)
            {
                Log.Write("Wheel lost focus; dismissed it.");
                Hide();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                _clickMode = false;
        };
    }

    public void ApplySettings(Settings settings)
    {
        _view.ApplySettings(settings);
        Width = Height = _view.Extent;
    }

    /// <summary>Drag mode: shows the wheel centred on a point given in physical screen pixels.</summary>
    public void ShowAt(int x, int y)
    {
        _dropTimer.Stop();
        _leaveTimer.Stop();
        _dropped = false;
        _clickMode = false;
        SetActivatable(false);
        _view.SetActions(Array.Empty<WheelAction>(), "Drag onto a button");
        _view.SetHot(-1);
        Reveal(x, y, keepOnScreen: false);
    }

    /// <summary>Click mode: shows the wheel for files that are already known, near a point in physical pixels.</summary>
    public void ShowForFiles(string[] files, int x, int y)
    {
        _dropTimer.Stop();
        _leaveTimer.Stop();
        _clickFiles = files;
        var (label, actions) = Formats.ActionsFor(files, CanHandle);
        Log.Write($"Wheel opened for {files.Length} file(s), {actions.Length} button(s).");
        _view.SetActions(actions, label);
        _view.SetHot(-1);

        SetActivatable(true);
        // Moving the wheel (rather than letting it hang off a screen edge) is safe here: nothing is
        // being dragged, so the cursor ending up over a button doesn't choose it.
        Reveal(x, y, keepOnScreen: true);
        _clickMode = true;
        WindowHelpers.ForceForeground(this);
        Keyboard.Focus(this);
    }

    /// <summary>Called when the user releases the left button after a trigger.</summary>
    public void OnDragEnded()
    {
        if (IsVisible && !_clickMode)
            _dropTimer.Start();
    }

    /// <summary>
    /// Show invisibly, move with Win32 (physical pixels), then reveal. Moving to a monitor with a
    /// different DPI makes WPF resize the window, so centre once more after that settles.
    /// </summary>
    private void Reveal(int x, int y, bool keepOnScreen)
    {
        Opacity = 0;
        Show();
        CenterOn(x, y, keepOnScreen);
        Opacity = 1;
        _view.PlayIntro();
        Dispatcher.BeginInvoke(() => CenterOn(x, y, keepOnScreen), DispatcherPriority.Loaded);
    }

    private void CenterOn(int x, int y, bool keepOnScreen)
    {
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (keepOnScreen)
        {
            var area = Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
            x = Math.Clamp(x, area.Left + w / 2, Math.Max(area.Left + w / 2, area.Right - w / 2));
            y = Math.Clamp(y, area.Top + h / 2, Math.Max(area.Top + h / 2, area.Bottom - h / 2));
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x - w / 2, y - h / 2, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Drag mode must never steal focus from the app being dragged from; click mode needs it for keys.</summary>
    private void SetActivatable(bool activatable)
    {
        if (_hwnd == IntPtr.Zero)
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex = activatable ? ex & ~Native.WS_EX_NOACTIVATE : ex | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    // --- Click mode ---

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_clickMode)
            _view.SetHot(_view.HitTest(e.GetPosition(_view)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_clickMode)
            return;
        e.Handled = true;
        Choose(_view.HitTest(e.GetPosition(_view)));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_clickMode)
            return;

        int count = _view.Actions.Count;
        int hot = _view.HotIndex;
        switch (e.Key)
        {
            case Key.Escape:
                Choose(-1);
                break;
            case Key.Enter or Key.Space:
                Choose(hot);
                break;
            // Clockwise / anticlockwise around the ring, starting from the top button.
            case Key.Right or Key.Down or Key.Tab when count > 0:
                _view.SetHot(hot < 0 ? 0 : (hot + 1) % count);
                break;
            case Key.Left or Key.Up when count > 0:
                _view.SetHot(hot < 0 ? count - 1 : (hot - 1 + count) % count);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Picks a button by index, or cancels for anything else (the centre, outside, -1).</summary>
    private void Choose(int index)
    {
        var files = _clickFiles;
        Hide();
        if (index >= 0 && index < _view.Actions.Count)
        {
            var action = _view.Actions[index];
            Log.Write($"Chose '{action.Label}' for {files.Length} file(s).");
            ActionChosen?.Invoke(files, action);
        }
        else
        {
            Log.Write("Wheel cancelled.");
        }
    }

    // --- Drag mode ---

    protected override void OnDragEnter(DragEventArgs e)
    {
        _leaveTimer.Stop();
        var files = GetFiles(e);
        var (label, actions) = files == null
            ? ("Not a file", Array.Empty<WheelAction>())
            : Formats.ActionsFor(files, CanHandle);
        Log.Write(files == null
            ? $"Drag entered the wheel without files (formats: {string.Join(", ", e.Data.GetFormats())})."
            : $"Drag entered the wheel: {files.Length} file(s), {actions.Length} button(s).");
        _view.SetActions(actions, label);
        UpdateHot(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        _leaveTimer.Stop();
        UpdateHot(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        _view.SetHot(-1);
        _leaveTimer.Start();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        _dropped = true;
        _dropTimer.Stop();
        _leaveTimer.Stop();
        e.Handled = true;

        var files = GetFiles(e);
        int index = _view.HitTest(e.GetPosition(_view));
        Hide();

        if (files != null && index >= 0 && index < _view.Actions.Count)
        {
            var action = _view.Actions[index];
            Log.Write($"Dropped {files.Length} file(s) on '{action.Label}': {string.Join(", ", files)}");
            ActionChosen?.Invoke(files, action);
        }
        else
        {
            Log.Write("Dropped on the cancel button; nothing done.");
        }
    }

    private void UpdateHot(DragEventArgs e)
    {
        int index = GetFiles(e) == null ? -1 : _view.HitTest(e.GetPosition(_view));
        _view.SetHot(index);

        // Always Copy, never Move: a Move result would tell Explorer to delete the original.
        e.Effects = index >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static string[]? GetFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
}
