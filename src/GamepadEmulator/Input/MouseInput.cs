using System.Runtime.InteropServices;

namespace GamepadEmulator.Input;

internal static class MouseInput
{
    public static void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                dwFlags = NativeMethods.MOUSEEVENTF_MOVE,
            },
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
