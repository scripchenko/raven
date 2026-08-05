using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private AppSettings _settings = AppSettings.CreateDefault();
    private readonly IWebViewSessionManager _webViewSessionManager;

    public MainWindowViewModel(
        IBuiltInServiceCatalog serviceCatalog,
        IWebViewSessionManager webViewSessionManager)
    {
        AvailableServices = serviceCatalog.All;
        _webViewSessionManager = webViewSessionManager;
        _webViewSessionManager.StateChanged += OnWebViewSessionStateChanged;
    }

    public ObservableCollection<ServiceInstance> Services { get; } = [];
    public IReadOnlyList<ServiceDefinition> AvailableServices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedService))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ServiceInstance? _selectedService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoForwardCommand))]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWebViewError))]
    [NotifyPropertyChangedFor(nameof(IsWebViewInitializing))]
    private WebViewSessionStatus _webViewStatus = WebViewSessionStatus.Uninitialized;

    [ObservableProperty]
    private string? _webViewErrorTitle;

    [ObservableProperty]
    private string? _webViewErrorMessage;

    [ObservableProperty]
    private string? _webViewErrorCode;

    [ObservableProperty]
    private string? _webViewRuntimeVersion;

    public bool HasSelectedService => SelectedService is not null;
    public bool HasWebViewError => WebViewStatus is WebViewSessionStatus.Offline or WebViewSessionStatus.Failed;
    public bool IsWebViewInitializing => WebViewStatus is WebViewSessionStatus.Uninitialized or WebViewSessionStatus.Initializing;

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

        ServiceInstance? restoredService = settings.RestoreLastService && settings.LastServiceId is Guid lastServiceId
            ? Services.FirstOrDefault(service => service.Id == lastServiceId)
            : null;
        SelectedService = restoredService ?? Services.FirstOrDefault();
    }

    public void SetRuntimeInfo(WebViewRuntimeInfo runtimeInfo)
    {
        ArgumentNullException.ThrowIfNull(runtimeInfo);
        WebViewRuntimeVersion = runtimeInfo.Version;
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

    public void Dispose()
    {
        _webViewSessionManager.StateChanged -= OnWebViewSessionStateChanged;
    }

    [RelayCommand(CanExecute = nameof(CanNavigateBack))]
    private void GoBack() => _webViewSessionManager.GoBack();

    private bool CanNavigateBack() => CanGoBack;

    [RelayCommand(CanExecute = nameof(CanNavigateForward))]
    private void GoForward() => _webViewSessionManager.GoForward();

    private bool CanNavigateForward() => CanGoForward;

    [RelayCommand]
    private void Reload() => _webViewSessionManager.Reload();

    [RelayCommand]
    private void NavigateHome() => _webViewSessionManager.NavigateHome();

    [RelayCommand]
    private void Retry() => _webViewSessionManager.Retry();

    private void OnWebViewSessionStateChanged(object? sender, WebViewSessionStateChangedEventArgs e)
    {
        CanGoBack = e.State.CanGoBack;
        CanGoForward = e.State.CanGoForward;
        IsLoading = e.State.IsLoading;
        WebViewErrorTitle = e.State.ErrorTitle;
        WebViewErrorMessage = e.State.ErrorMessage;
        WebViewErrorCode = e.State.ErrorCode;
        WebViewStatus = e.State.Status;
    }
}
