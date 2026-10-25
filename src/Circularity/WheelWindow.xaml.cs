using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly DropFeedback _feedback = new();
    private readonly DispatcherTimer _dropTimer;
    private readonly DispatcherTimer _leaveTimer;
    private bool _dropped;
    private bool _clickMode;
    private bool _closing;
    private string[] _clickFiles = Array.Empty<string>();
    private string? _lastDragLabel;
    private Settings _settings = new();
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
                Dismiss();
            }
        };

        // Leaving the wheel mid-drag dismisses it. The short delay absorbs the leave/enter
        // pair OLE can send as the wheel appears under the cursor.
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            Log.Write("Dragged off the wheel; dismissed it.");
            Dismiss();
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
                Dismiss();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                _clickMode = false;
        };
    }

    /// <summary>Showing and not on its way out.</summary>
    public bool IsOpen => IsVisible && !_closing;

    /// <summary>
    /// Fades the wheel out, then hides it. While fading it lets the mouse and drags pass straight through,
    /// so it never gets in the way of what's underneath.
    /// </summary>
    private void Dismiss()
    {
        if (!IsVisible || _closing)
            return;
        _closing = true;
        _clickMode = false;
        SetClickThrough(true);
        _view.PlayOutro(() =>
        {
            // A new Show during the fade cancels it.
            if (!_closing)
                return;
            _closing = false;
            Hide();
            SetClickThrough(false);
        });
    }

    private void SetClickThrough(bool clickThrough)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex = clickThrough ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    public void ApplySettings(Settings settings)
    {
        _settings = settings;
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
        _view.ArcAbove = ArcShouldOpenAbove(x, y);
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
        var (label, actions) = Formats.ActionsFor(files, CanHandle, _settings);
        Log.Write($"Wheel opened for {files.Length} file(s) ({string.Join(", ", files.Select(Formats.NormalizedExtension).Distinct())}), {actions.Length} button(s) ({label}).");
        // Decided before the wheel is kept on screen, so near the bottom edge it flips rather than
        // being pushed a long way up from the cursor to make room for the arc.
        _view.ArcAbove = ArcShouldOpenAbove(x, y);
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

    /// <summary>
    /// Whether the "More" arc would run off the bottom of the screen if it opened below a wheel centred
    /// here (physical pixels), with room for it above instead. In drag mode the wheel can't move away
    /// from the cursor, so flipping is the only way to keep the arc reachable.
    /// </summary>
    private bool ArcShouldOpenAbove(int x, int y)
    {
        var area = Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        double reach = _view.ArcReach * Native.ScaleAt(x, y);
        return y + reach > area.Bottom && y - reach >= area.Top;
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
        _closing = false;
        SetClickThrough(false);
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

        var ring = _view.RingOrder;
        var arc = _view.ArcOrder;
        int hot = _view.HotIndex;
        int inArc = IndexOf(arc, hot);
        int onRing = IndexOf(ring, hot);
        // Towards the arc and back towards the wheel: down and up, or the other way when it opens above.
        var intoArc = _view.ArcAbove ? Key.Up : Key.Down;
        var outOfArc = _view.ArcAbove ? Key.Down : Key.Up;
        switch (e.Key)
        {
            // In the arc, Left and Right go the way they point (the arc runs right to left), and
            // heading back towards the wheel, or Esc, steps out to "More" rather than closing it all.
            case Key.Left or Key.Tab when inArc >= 0:
                _view.SetHot(arc[(inArc + 1) % arc.Count]);
                break;
            case Key.Right when inArc >= 0:
                _view.SetHot(arc[(inArc - 1 + arc.Count) % arc.Count]);
                break;
            case var key when key == outOfArc && inArc >= 0:
                _view.CollapseToMore();
                break;
            case Key.Escape when _view.IsExpanded:
                _view.CollapseToMore();
                break;
            case var key when (key == intoArc || key is Key.Enter or Key.Space) && hot == WheelView.MoreIndex:
                EnterArc();
                break;
            case Key.Escape:
                Choose(-1);
                break;
            case Key.Enter or Key.Space:
                Choose(hot);
                break;
            // Around the ring: clockwise / anticlockwise, starting from the top button.
            case Key.Right or Key.Down or Key.Tab when ring.Count > 0:
                _view.SetHot(ring[onRing < 0 ? 0 : (onRing + 1) % ring.Count]);
                break;
            case Key.Left or Key.Up when ring.Count > 0:
                _view.SetHot(ring[onRing < 0 ? ring.Count - 1 : (onRing - 1 + ring.Count) % ring.Count]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Opens the arc (if it isn't already) and moves onto its middle button, the one right under "More".</summary>
    private void EnterArc()
    {
        var arc = _view.ArcOrder;
        _view.SetHot(WheelView.MoreIndex);
        if (arc.Count > 0)
            _view.SetHot(arc[(arc.Count - 1) / 2]);
    }

    private static int IndexOf(IReadOnlyList<int> targets, int target)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] == target)
                return i;
        }
        return -1;
    }

    /// <summary>Picks a button by index, or cancels for anything else (the centre, outside, -1).</summary>
    private void Choose(int index)
    {
        if (index == WheelView.MoreIndex)
        {
            // Clicking "More" just makes sure its arc is open; the pointer is already on its way there.
            _view.SetHot(WheelView.MoreIndex);
            return;
        }

        var files = _clickFiles;
        Dismiss();
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
            : Formats.ActionsFor(files, CanHandle, _settings);
        // Crossing between the disc and the arc counts as leaving one element and entering another,
        // so only log the first entry for these files.
        string entry = files == null
            ? $"Drag entered the wheel without files (formats: {string.Join(", ", e.Data.GetFormats())})."
            : $"Drag entered the wheel: {files.Length} file(s) ({string.Join(", ", files.Select(Formats.NormalizedExtension).Distinct())}), {actions.Length} button(s) ({label}).";
        if (!actions.SequenceEqual(_view.Actions) || label != _lastDragLabel)
            Log.Write(entry);
        _lastDragLabel = label;
        _view.SetActions(actions, label);
        _feedback.Enter(_hwnd, e.Data, DragDropEffects.None);
        UpdateHot(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        _leaveTimer.Stop();
        UpdateHot(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        // The highlight stays put: moving between the disc and the arc raises a leave and an enter
        // back to back, and clearing it here would make the button flicker (and close the arc). If the
        // drag really has left, the timer dismisses the whole wheel anyway.
        _feedback.Leave(e.Data);
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
        // Tells the app the file came from what happened: a copy was made, or nothing (cancel, More).
        // Never Move, which would tell Explorer to delete the original.
        e.Effects = files != null && index >= 0 && index < _view.Actions.Count ? DragDropEffects.Copy : DragDropEffects.None;
        _feedback.Drop(e.Data, e.Effects);
        Dismiss();

        if (files != null && index >= 0 && index < _view.Actions.Count)
        {
            var action = _view.Actions[index];
            Log.Write($"Dropped {files.Length} file(s) on '{action.Label}': {string.Join(", ", files)}");
            ActionChosen?.Invoke(files, action);
        }
        else
        {
            Log.Write(index == WheelView.MoreIndex ? "Dropped on More; nothing done." : "Dropped on the cancel button; nothing done.");
        }
    }

    private void UpdateHot(DragEventArgs e)
    {
        int index = GetFiles(e) == null ? -1 : _view.HitTest(e.GetPosition(_view));
        _view.SetHot(index);

        // Always Copy, never Move: a Move result would tell Explorer to delete the original.
        // "More" is a live button too, so it doesn't get the no-entry cursor. Neither does the centre
        // when Windows is drawing the drag: there the label says "Cancel" instead, which Windows only
        // shows for an accepted drop. The drop itself still reports nothing done (see OnDrop).
        bool labelled = DropFeedback.IsShownFor(e.Data);        e.Effects = index >= 0 || index == WheelView.MoreIndex || (index == WheelView.CenterIndex && labelled)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;

        _feedback.Over(e.Effects);
        var (icon, message, insert) = DescribeTarget(index);
        _feedback.Describe(e.Data, icon, message, insert);
    }

    /// <summary>The label under the dragged file: what letting go here will do.</summary>
    private (DropFeedback.Icon, string, string) DescribeTarget(int index)
    {
        if (index == WheelView.CenterIndex)
            return (DropFeedback.Icon.Label, "Cancel", "");
        if (index == WheelView.MoreIndex)
            return (DropFeedback.Icon.Label, "More options", "");
        if (index < 0 || index >= _view.Actions.Count)
            return (DropFeedback.Icon.Invalid, "", "");

        var action = _view.Actions[index];
        return action.Kind switch
        {
            WheelActionKind.Convert => (DropFeedback.Icon.Copy, "Convert to %1", action.Label),
            WheelActionKind.Combine => (DropFeedback.Icon.Copy, "Combine into %1", "one PDF"),
            _ when action.OpensDialog => (DropFeedback.Icon.Copy, "%1…", action.Label),
            _ => (DropFeedback.Icon.Copy, "%1", action.Label),
        };
    }

    private static string[]? GetFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
}
