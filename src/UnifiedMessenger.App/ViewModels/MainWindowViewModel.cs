using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;

namespace UnifiedMessenger.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private AppSettings _settings = AppSettings.CreateDefault();

    public MainWindowViewModel(IBuiltInServiceCatalog serviceCatalog)
    {
        AvailableServices = serviceCatalog.All;
    }

    public ObservableCollection<ServiceInstance> Services { get; } = [];
    public IReadOnlyList<ServiceDefinition> AvailableServices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedService))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ServiceInstance? _selectedService;

    public bool HasSelectedService => SelectedService is not null;

    public string WindowTitle => SelectedService is null
        ? "UnifiedMessenger"
        : $"{SelectedService.DisplayName} — UnifiedMessenger";

    public WindowSettings WindowSettings => _settings.Window;

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        Services.Clear();
        foreach (ServiceInstance service in ServiceInstanceManager.Sort(settings.Services).Where(service => service.IsEnabled))
        {
            Services.Add(service);
        }

        SelectedService = settings.RestoreLastService && settings.LastServiceId is Guid lastServiceId
            ? Services.FirstOrDefault(service => service.Id == lastServiceId)
            : null;
    }

    public void UpdateWindowSettings(double width, double height, double left, double top, bool isMaximized)
    {
        if (!double.IsNaN(width) && width >= 880)
        {
            _settings.Window.Width = width;
        }

        if (!double.IsNaN(height) && height >= 560)
        {
            _settings.Window.Height = height;
        }

        _settings.Window.Left = left;
        _settings.Window.Top = top;
        _settings.Window.IsMaximized = isMaximized;
    }

    public AppSettings CreateSettingsSnapshot()
    {
        _settings.Services = Services.ToList();
        _settings.LastServiceId = SelectedService?.Id;
        return _settings;
    }
}
