using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GamepadEmulator.Input;

internal static class MouseInput
{
    public static void MoveRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;

        Send(new NativeMethods.MOUSEINPUT { dx = dx, dy = dy, dwFlags = NativeMethods.MOUSEEVENTF_MOVE });
    }

    public static void SendButtonUp(MouseButtons button)
    {
        var flag = button switch
        {
            MouseButtons.Left => NativeMethods.MOUSEEVENTF_LEFTUP,
            MouseButtons.Right => NativeMethods.MOUSEEVENTF_RIGHTUP,
            MouseButtons.Middle => NativeMethods.MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };

        if (flag == 0)
            return;

        Send(new NativeMethods.MOUSEINPUT { dwFlags = flag });
    }

    private static void Send(NativeMethods.MOUSEINPUT mi)
    {
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, mi = mi };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
