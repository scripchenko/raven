using System.Windows;

namespace UnifiedMessenger.App.Services.Tray;

public interface IWindowActivationService
{
    bool IsMainWindowActive { get; }
    bool IsMainWindowVisible { get; }
    Guid? SelectedServiceId { get; }
    void Attach(Window window, Func<Guid?> selectedServiceId, Action<Guid> selectService);
    void Detach(Window window);
    void ShowAndActivate(Guid? serviceInstanceId = null);
}
