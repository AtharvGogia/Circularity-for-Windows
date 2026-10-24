using System;
using System.Windows.Interop;

namespace Circularity;

/// <summary>A system-wide keyboard shortcut (Ctrl+Alt+C by default), received on a hidden message window.</summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x0C1C;
    private static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE

    private readonly HwndSource _window;

    public event Action? Pressed;

    public bool IsRegistered { get; private set; }

    public GlobalHotkey()
    {
        _window = new HwndSource(new HwndSourceParameters("Circularity hotkey") { ParentWindow = MessageOnlyParent });
        _window.AddHook(WndProc);
    }

    /// <summary>Returns false if another app already owns the shortcut.</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        IsRegistered = Native.RegisterHotKey(_window.Handle, Id, modifiers | Native.MOD_NOREPEAT, virtualKey);
        return IsRegistered;
    }

    public void Unregister()
    {
        if (IsRegistered)
            Native.UnregisterHotKey(_window.Handle, Id);
        IsRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }
}
