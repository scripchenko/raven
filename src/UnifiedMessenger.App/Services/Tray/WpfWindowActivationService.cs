using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace UnifiedMessenger.App.Services.Tray;

public sealed class WpfWindowActivationService : IWindowActivationService
{
    private Window? _window;
    private Func<Guid?>? _selectedServiceId;
    private Action<Guid>? _selectService;

    public bool IsMainWindowActive => _window is { IsVisible: true, IsActive: true };
    public bool IsMainWindowVisible => _window is { IsVisible: true };
    public Guid? SelectedServiceId => _selectedServiceId?.Invoke();

    public void Attach(Window window, Func<Guid?> selectedServiceId, Action<Guid> selectService)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(selectedServiceId);
        ArgumentNullException.ThrowIfNull(selectService);
        _window = window;
        _selectedServiceId = selectedServiceId;
        _selectService = selectService;
    }

    public void Detach(Window window)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
            _selectedServiceId = null;
            _selectService = null;
        }
    }

    public void ShowAndActivate(Guid? serviceInstanceId = null)
    {
        Window? window = _window;
        if (window is null)
        {
            return;
        }

        _ = window.Dispatcher.InvokeAsync(
            () =>
            {
                if (serviceInstanceId is Guid id)
                {
                    _selectService?.Invoke(id);
                }

                if (!window.IsVisible)
                {
                    window.Show();
                }

                IntPtr windowHandle = new WindowInteropHelper(window).EnsureHandle();
                if (window.WindowState == WindowState.Minimized
                    || NativeWindowActivation.IsMinimized(windowHandle))
                {
                    window.WindowState = WindowState.Normal;
                }

                NativeWindowActivation.RestoreAndActivate(windowHandle);
                _ = window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                _ = window.Focus();
            },
            DispatcherPriority.Normal);
    }
}
