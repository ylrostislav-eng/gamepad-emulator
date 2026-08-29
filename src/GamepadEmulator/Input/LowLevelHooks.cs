using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GamepadEmulator.Input;

public sealed class LowLevelHooks : IDisposable
{
    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly NativeMethods.HookProc _mouseProc;
    private IntPtr _keyboardHookHandle;
    private IntPtr _mouseHookHandle;

    private int _lastMouseX;
    private int _lastMouseY;
    private bool _haveLastMousePos;

    public event Action<Keys>? KeyDown;
    public event Action<Keys>? KeyUp;
    public event Action<MouseButtons>? MouseButtonDown;
    public event Action<MouseButtons>? MouseButtonUp;
    public event Action<int, int>? MouseMoveDelta;

    public Func<Keys, bool>? SuppressKey;
    public Func<MouseButtons, bool>? SuppressMouseButton;

    public LowLevelHooks()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Install()
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        var hModule = NativeMethods.GetModuleHandle(curModule.ModuleName);

        _keyboardHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _keyboardProc, hModule, 0);
        if (_keyboardHookHandle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install keyboard hook: " + Marshal.GetLastWin32Error());

        _mouseHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, hModule, 0);
        if (_mouseHookHandle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install mouse hook: " + Marshal.GetLastWin32Error());
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var key = (Keys)data.vkCode;
            var msg = wParam.ToInt32();

            var isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            var isUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

            if (isDown)
                KeyDown?.Invoke(key);
            else if (isUp)
                KeyUp?.Invoke(key);

            if ((isDown || isUp) && (SuppressKey?.Invoke(key) ?? false))
                return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();

            if ((data.flags & NativeMethods.LLMHF_INJECTED) == 0 && msg == NativeMethods.WM_MOUSEMOVE)
            {
                if (_haveLastMousePos)
                {
                    var dx = data.pt.x - _lastMouseX;
                    var dy = data.pt.y - _lastMouseY;
                    if (dx != 0 || dy != 0)
                        MouseMoveDelta?.Invoke(dx, dy);
                }

                _lastMouseX = data.pt.x;
                _lastMouseY = data.pt.y;
                _haveLastMousePos = true;
            }

            var button = MessageToButton(msg);
            if (button != MouseButtons.None)
            {
                var isDown = msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN;
                if (isDown)
                    MouseButtonDown?.Invoke(button);
                else
                    MouseButtonUp?.Invoke(button);

                if (SuppressMouseButton?.Invoke(button) ?? false)
                    return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private static MouseButtons MessageToButton(int msg) => msg switch
    {
        NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP => MouseButtons.Left,
        NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP => MouseButtons.Right,
        NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => MouseButtons.Middle,
        _ => MouseButtons.None,
    };

    public void Dispose()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }

        if (_mouseHookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }
    }
}
