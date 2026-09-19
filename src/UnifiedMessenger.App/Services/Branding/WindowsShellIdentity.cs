using System.IO;
using System.Runtime.InteropServices;

namespace UnifiedMessenger.App.Services.Branding;

internal static class WindowsShellIdentity
{
    internal const string ApplicationUserModelId = "Scripchenko.Raven";
    internal const string RelaunchIconRelativePath = @"Assets\Branding\lantern_system.ico";

    private static readonly Guid PropertyStoreInterfaceId =
        new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey RelaunchCommandKey = new(2);
    private static readonly PropertyKey RelaunchIconResourceKey = new(3);
    private static readonly PropertyKey RelaunchDisplayNameResourceKey = new(4);
    private static readonly PropertyKey AppUserModelIdKey = new(5);

    internal static bool TryInitializeProcess()
    {
        try
        {
            return SetCurrentProcessExplicitAppUserModelID(ApplicationUserModelId) >= 0;
        }
        catch (Exception exception) when (IsUnavailableShellApi(exception))
        {
            return false;
        }
    }

    internal static bool TryApplyToWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return false;
        }

        IPropertyStore? propertyStore = null;
        try
        {
            Guid interfaceId = PropertyStoreInterfaceId;
            if (SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out propertyStore) < 0
                || propertyStore is null)
            {
                return false;
            }

            // Relaunch properties must be available before the explicit window AppUserModelID
            // is set because setting the ID asks the taskbar to refresh the window identity.
            if (!TrySetString(propertyStore, RelaunchCommandKey, CreateRelaunchCommand(Environment.ProcessPath))
                || !TrySetString(propertyStore, RelaunchDisplayNameResourceKey, BrandIdentity.DisplayName)
                || !TrySetString(
                    propertyStore,
                    RelaunchIconResourceKey,
                    CreateRelaunchIconResource(AppContext.BaseDirectory))
                || !TrySetString(propertyStore, AppUserModelIdKey, ApplicationUserModelId))
            {
                return false;
            }

            return propertyStore.Commit() >= 0;
        }
        catch (Exception exception) when (IsUnavailableShellApi(exception))
        {
            return false;
        }
        finally
        {
            if (propertyStore is not null && Marshal.IsComObject(propertyStore))
            {
                Marshal.FinalReleaseComObject(propertyStore);
            }
        }
    }

    internal static string CreateRelaunchIconResource(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        string iconPath = Path.GetFullPath(Path.Combine(applicationDirectory, RelaunchIconRelativePath));
        return $"{iconPath},0";
    }

    internal static string CreateRelaunchCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{Path.GetFullPath(executablePath)}\"";
    }

    private static bool TrySetString(IPropertyStore propertyStore, PropertyKey propertyKey, string value)
    {
        PropVariant propertyValue = PropVariant.CreateString(value);

        try
        {
            return propertyStore.SetValue(ref propertyKey, ref propertyValue) >= 0;
        }
        finally
        {
            propertyValue.Dispose();
        }
    }

    private static bool IsUnavailableShellApi(Exception exception) => exception is
        COMException or
        DllNotFoundException or
        EntryPointNotFoundException or
        PlatformNotSupportedException;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey propertyKey);

        [PreserveSig]
        int GetValue(ref PropertyKey propertyKey, out PropVariant propertyValue);

        [PreserveSig]
        int SetValue(ref PropertyKey propertyKey, ref PropVariant propertyValue);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(uint propertyId)
    {
        private readonly Guid _formatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private readonly uint _propertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant : IDisposable
    {
        private const ushort UnicodeString = 31;

        [FieldOffset(0)]
        private ushort _variantType;

        [FieldOffset(8)]
        private IntPtr _pointerValue;

        public static PropVariant CreateString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return new PropVariant
            {
                _variantType = UnicodeString,
                _pointerValue = Marshal.StringToCoTaskMemUni(value)
            };
        }

        public void Dispose()
        {
            if (_pointerValue == IntPtr.Zero)
            {
                return;
            }

            Marshal.FreeCoTaskMem(_pointerValue);
            _pointerValue = IntPtr.Zero;
            _variantType = 0;
        }
    }
}
