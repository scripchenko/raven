using System.Runtime.InteropServices;

namespace UnifiedMessenger.App.Services.Branding;

internal static class NativeWindowCaption
{
    internal const long DialogModalFrameExtendedStyle = 0x00000001L;
    private const int ExtendedWindowStyleIndex = -20;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;

    internal static bool TryHideBranding(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            Marshal.SetLastPInvokeError(0);
            IntPtr currentStyle = GetWindowLongPtr(windowHandle, ExtendedWindowStyleIndex);
            if (currentStyle == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }

            long updatedStyle = currentStyle.ToInt64() | DialogModalFrameExtendedStyle;
            if (updatedStyle != currentStyle.ToInt64())
            {
                Marshal.SetLastPInvokeError(0);
                IntPtr previousStyle = SetWindowLongPtr(
                    windowHandle,
                    ExtendedWindowStyleIndex,
                    new IntPtr(updatedStyle));
                if (previousStyle == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
                {
                    return false;
                }
            }

            return SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NoSize | NoMove | NoZOrder | NoActivate | FrameChanged);
        }
        catch (Exception exception) when (exception is
            DllNotFoundException or
            EntryPointNotFoundException or
            PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));

    private static IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
