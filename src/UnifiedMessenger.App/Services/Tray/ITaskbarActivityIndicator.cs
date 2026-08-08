using System.Windows;

namespace UnifiedMessenger.App.Services.Tray;

public interface ITaskbarActivityIndicator
{
    bool HasActivity { get; }
    void Attach(Window window);
    void Detach(Window window);
    void SetHasActivity(bool hasActivity);
}
