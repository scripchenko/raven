using System.Windows;

namespace UnifiedMessenger.App.Services.Tray;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
    }

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return System.Windows.Application.Current.Dispatcher.CheckAccess()
            ? Task.FromResult(action())
            : System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;
    }
}
