namespace UnifiedMessenger.App.Services.Tray;

public interface IApplicationTrayCoordinator : IDisposable
{
    bool IsAvailable { get; }
    void Initialize();
    void BeginShutdown();
    bool TryShowCloseToTrayHint();
}
