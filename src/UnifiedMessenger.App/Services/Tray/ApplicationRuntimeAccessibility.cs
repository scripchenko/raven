namespace UnifiedMessenger.App.Services.Tray;

internal static class ApplicationRuntimeAccessibility
{
    internal static bool CanHideMainWindow(bool closeToTrayEnabled, bool trayAvailable) =>
        closeToTrayEnabled && trayAvailable;

    internal static bool HasUsableEntryPoint(
        bool mainWindowVisible,
        bool startupWindowVisible,
        bool trayAvailable) =>
        mainWindowVisible || startupWindowVisible || trayAvailable;
}
