using System.Windows;
using System.Windows.Shell;

namespace UnifiedMessenger.App.Services.Tray;

public sealed class WpfTaskbarActivityIndicator : ITaskbarActivityIndicator
{
    private Window? _window;

    public bool HasActivity { get; private set; }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        window.TaskbarItemInfo ??= new TaskbarItemInfo();
        ApplyOverlay();
    }

    public void Detach(Window window)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }
    }

    public void SetHasActivity(bool hasActivity)
    {
        HasActivity = hasActivity;
        ApplyOverlay();
    }

    private void ApplyOverlay()
    {
        if (_window?.TaskbarItemInfo is TaskbarItemInfo taskbarItem)
        {
            // The final Raven hybrid mapping deliberately keeps the historical
            // bracket unobstructed in icon-only Windows shell surfaces.
            taskbarItem.Overlay = null;
        }
    }
}
