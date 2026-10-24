using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Circularity;

internal enum GestureTrigger { RightClick, Shift }

/// <summary>
/// System-wide low-level mouse and keyboard hooks that open the wheel during a file drag.
/// A drag is the left button held and moved past the system drag threshold. While dragging:
///
///  - pressing the right button, or
///  - pressing Shift (the touchpad-friendly fallback; not if Shift was already held when the
///    drag started, because that's how Explorer forces a move)
///
/// raises <see cref="Triggered"/>. That right-click or Shift press is swallowed, so the app being
/// dragged from (usually Explorer) never sees it and keeps its drag alive unchanged.
///
/// Both hooks live on one dedicated thread with its own message loop, so their callbacks never
/// race each other. Windows silently removes a low-level hook whose callback is too slow, so
/// they must never wait on the UI thread.
/// </summary>
internal sealed class GestureHook : IDisposable
{
    /// <summary>Raised on the hook thread with the cursor position in physical pixels.</summary>
    public event Action<int, int, GestureTrigger>? Triggered;

    /// <summary>Raised on the hook thread when the left button is released after a trigger.</summary>
    public event Action? DragEnded;

    /// <summary>Asked on the hook thread before triggering; return false to let the input through.</summary>
    public Func<GestureTrigger, bool> ShouldArm { get; set; } = _ => true;

    private readonly Thread _thread;
    private readonly int _dragThresholdX = Native.GetSystemMetrics(Native.SM_CXDRAG);
    private readonly int _dragThresholdY = Native.GetSystemMetrics(Native.SM_CYDRAG);

    // Must stay referenced or the GC collects them out from under the hooks.
    private Native.LowLevelMouseProc? _mouseProc;
    private Native.LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private uint _threadId;

    private bool _leftDown;
    private bool _dragging;
    private bool _triggeredThisDrag;
    private bool _swallowRightUp;
    private int _downX, _downY;

    private bool _shiftDown;
    private bool _shiftHeldAtDragStart;
    private uint _swallowedShiftKey; // Virtual-key code of a swallowed Shift press, until its key-up.

    public GestureHook()
    {
        using var ready = new ManualResetEventSlim();
        int error = 0;

        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            IntPtr module = Native.GetModuleHandle(null);
            _mouseProc = MouseProc;
            _keyboardProc = KeyboardProc;
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            if (_mouseHook != IntPtr.Zero)
                _keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
            error = Marshal.GetLastPInvokeError();
            ready.Set();

            if (_mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero)
            {
                while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            }

            if (_keyboardHook != IntPtr.Zero)
                Native.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != IntPtr.Zero)
                Native.UnhookWindowsHookEx(_mouseHook);
        })
        {
            IsBackground = true,
            Name = "GestureHook",
        };
        _thread.Start();
        ready.Wait();

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            _thread.Join(1000);
            throw new Win32Exception(error, "Could not install the low-level mouse/keyboard hooks.");
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    _leftDown = true;
                    _dragging = false;
                    _triggeredThisDrag = false;
                    _shiftHeldAtDragStart = _shiftDown;
                    _downX = info.pt.X;
                    _downY = info.pt.Y;
                    break;

                case Native.WM_MOUSEMOVE:
                    if (_leftDown && !_dragging &&
                        (Math.Abs(info.pt.X - _downX) > _dragThresholdX || Math.Abs(info.pt.Y - _downY) > _dragThresholdY))
                    {
                        _dragging = true;
                    }
                    break;

                case Native.WM_LBUTTONUP:
                    bool hadTrigger = _triggeredThisDrag;
                    _leftDown = false;
                    _dragging = false;
                    _triggeredThisDrag = false;
                    if (hadTrigger)
                        DragEnded?.Invoke();
                    break;

                case Native.WM_RBUTTONDOWN:
                    if (_dragging && ShouldArm(GestureTrigger.RightClick))
                    {
                        _swallowRightUp = true;
                        _triggeredThisDrag = true;
                        Triggered?.Invoke(info.pt.X, info.pt.Y, GestureTrigger.RightClick);
                        return (IntPtr)1;
                    }
                    break;

                case Native.WM_RBUTTONUP:
                    if (_swallowRightUp)
                    {
                        _swallowRightUp = false;
                        return (IntPtr)1;
                    }
                    break;
            }
        }

        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (key.vkCode is Native.VK_LSHIFT or Native.VK_RSHIFT)
            {
                int message = (int)wParam;
                bool isDown = message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                bool isUp = message is Native.WM_KEYUP or Native.WM_SYSKEYUP;

                if (isDown)
                {
                    // Auto-repeat of a press we already swallowed.
                    if (_swallowedShiftKey == key.vkCode)
                        return (IntPtr)1;

                    bool wasDown = _shiftDown;
                    _shiftDown = true;
                    if (!wasDown && _dragging && !_shiftHeldAtDragStart && ShouldArm(GestureTrigger.Shift))
                    {
                        _swallowedShiftKey = key.vkCode;
                        _triggeredThisDrag = true;
                        Native.GetCursorPos(out var cursor);
                        Triggered?.Invoke(cursor.X, cursor.Y, GestureTrigger.Shift);
                        return (IntPtr)1;
                    }
                }
                else if (isUp)
                {
                    _shiftDown = false;
                    if (_swallowedShiftKey == key.vkCode)
                    {
                        _swallowedShiftKey = 0;
                        return (IntPtr)1;
                    }
                }
            }
        }

        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
