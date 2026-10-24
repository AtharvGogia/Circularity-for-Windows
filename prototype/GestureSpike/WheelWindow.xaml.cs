using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GestureSpike;

/// <summary>
/// The transparent, never-focused window that hosts the wheel. It is a real OLE drop target:
/// the user keeps dragging their file(s) onto a button and releases, and we receive the files
/// through a normal drop. The centre is a cancel button, which is where the cursor starts.
/// Dragging off the wheel dismisses it and lets the drag carry on as if nothing happened.
/// </summary>
internal partial class WheelWindow : Window
{
    /// <summary>Raised when files are dropped on a button.</summary>
    public event Action<string[], WheelAction>? ActionChosen;

    private readonly WheelView _view = new();
    private readonly DispatcherTimer _dropTimer;
    private readonly DispatcherTimer _leaveTimer;
    private bool _dropped;
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
    }

    public void ApplySettings(Settings settings)
    {
        _view.ApplySettings(settings);
        Width = Height = _view.Extent;
    }

    /// <summary>Shows the wheel centred on a point given in physical screen pixels.</summary>
    public void ShowAt(int x, int y)
    {
        _dropTimer.Stop();
        _leaveTimer.Stop();
        _dropped = false;
        _view.SetActions(Array.Empty<WheelAction>(), "Drag onto a button");
        _view.SetHot(-1);

        // Show invisibly, move with Win32 (physical pixels), then reveal. Moving to a monitor with a
        // different DPI makes WPF resize the window, so centre once more after that settles.
        Opacity = 0;
        Show();
        CenterOn(x, y);
        Opacity = 1;
        _view.PlayIntro();
        Dispatcher.BeginInvoke(() => CenterOn(x, y), DispatcherPriority.Loaded);
    }

    /// <summary>Called when the user releases the left button after a trigger.</summary>
    public void OnDragEnded()
    {
        if (IsVisible)
            _dropTimer.Start();
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
        _leaveTimer.Stop();
        var files = GetFiles(e);
        var (label, actions) = files == null
            ? ("Not a file", Array.Empty<WheelAction>())
            : Formats.ActionsFor(files);
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
