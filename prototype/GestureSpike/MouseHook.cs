using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace GestureSpike;

/// <summary>
/// System-wide low-level mouse hook that spots the "rocker" chord: the left button
/// is held and dragged past the system drag threshold, then the right button is pressed.
///
/// When the chord fires, the right-button down/up pair is swallowed so the app being
/// dragged from (usually Explorer) never sees it and keeps its drag alive.
///
/// The hook lives on its own thread with its own message loop. Windows silently removes
/// a low-level hook whose callback is too slow, so it must never wait on the UI thread.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    /// <summary>Raised on the hook thread with the cursor position in physical pixels.</summary>
    public event Action<int, int>? ChordDetected;

    /// <summary>Raised on the hook thread when the left button is released after a chord.</summary>
    public event Action? ChordDragEnded;

    /// <summary>Asked on the hook thread right before arming; return false to let the click through.</summary>
    public Func<bool> ShouldArm { get; set; } = () => true;

    private readonly Thread _thread;
    private readonly int _dragThresholdX = Native.GetSystemMetrics(Native.SM_CXDRAG);
    private readonly int _dragThresholdY = Native.GetSystemMetrics(Native.SM_CYDRAG);

    private Native.LowLevelMouseProc? _proc; // Must stay referenced or the GC collects it under the hook.
    private IntPtr _hook;
    private uint _threadId;

    private bool _leftDown;
    private bool _dragging;
    private bool _chordFired;
    private bool _swallowRightUp;
    private int _downX, _downY;

    public MouseHook()
    {
        using var ready = new ManualResetEventSlim();
        int error = 0;

        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = HookProc;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            error = Marshal.GetLastPInvokeError();
            ready.Set();
            if (_hook == IntPtr.Zero)
                return;

            while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

            Native.UnhookWindowsHookEx(_hook);
        })
        {
            IsBackground = true,
            Name = "MouseHook",
        };
        _thread.Start();
        ready.Wait();

        if (_hook == IntPtr.Zero)
            throw new Win32Exception(error, "Could not install the low-level mouse hook.");
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    _leftDown = true;
                    _dragging = false;
                    _chordFired = false;
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
                    bool hadChord = _chordFired;
                    _leftDown = false;
                    _dragging = false;
                    _chordFired = false;
                    if (hadChord)
                        ChordDragEnded?.Invoke();
                    break;

                case Native.WM_RBUTTONDOWN:
                    if (_chordFired)
                    {
                        // Extra right-clicks during the same drag: keep them away from the source app too.
                        _swallowRightUp = true;
                        return (IntPtr)1;
                    }
                    if (_dragging && ShouldArm())
                    {
                        _chordFired = true;
                        _swallowRightUp = true;
                        ChordDetected?.Invoke(info.pt.X, info.pt.Y);
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

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}
