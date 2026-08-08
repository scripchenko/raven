namespace UnifiedMessenger.App.Services.Tray;

public interface IUiDispatcher
{
    void Post(Action action);
    Task<T> InvokeAsync<T>(Func<T> action);
}
