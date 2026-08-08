namespace UnifiedMessenger.App.Services.Tray;

public interface IApplicationTrayCoordinator : IDisposable
{
    void Initialize();
    void BeginShutdown();
    bool TryShowCloseToTrayHint();
}
