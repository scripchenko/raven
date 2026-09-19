using System.Runtime.InteropServices;

namespace UnifiedMessenger.App.Services.Tray;

internal static class NativeWindowActivation
{
    private const int RestoreWindow = 9;

    internal static bool IsMinimized(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero && IsIconic(windowHandle);

    internal static void RestoreAndActivate(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        _ = ShowWindowAsync(windowHandle, RestoreWindow);
        _ = SetForegroundWindow(windowHandle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
