using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.ViewModels;

public sealed partial class SettingsAccountViewModel : ObservableObject, IDisposable
{
    private readonly SettingsViewModel _owner;
    private bool _disposed;

    internal SettingsAccountViewModel(
        SettingsViewModel owner,
        ServiceInstance service,
        string serviceTypeLabel)
    {
        _owner = owner;
        Service = service;
        ServiceTypeLabel = serviceTypeLabel;
        Service.PropertyChanged += OnServicePropertyChanged;
    }

    public ServiceInstance Service { get; }
    public string DisplayName => Service.DisplayName;
    public ServiceType ServiceType => Service.ServiceType;
    public string ServiceTypeLabel { get; }
    public bool IsEnabled => Service.IsEnabled;
    public bool IsMuted => Service.IsMuted;
    public bool CanMoveUp => _owner.CanMove(Service, -1);
    public bool CanMoveDown => _owner.CanMove(Service, 1);

    [RelayCommand]
    private void Open() => _owner.OpenAccount(Service);

    [RelayCommand]
    private void Rename() => _owner.RequestRename(Service);

    [RelayCommand]
    private void SetEnabled(bool? value)
    {
        if (value is bool isEnabled && isEnabled != Service.IsEnabled)
        {
            _owner.RequestSetEnabled(Service, isEnabled);
        }
    }

    [RelayCommand]
    private async Task SetMuted(bool? value)
    {
        if (value is bool isMuted && isMuted != Service.IsMuted)
        {
            await _owner.SetMutedAsync(Service, isMuted);
        }
    }

    [RelayCommand]
    private async Task MoveUp() => await _owner.MoveAsync(Service, -1);

    [RelayCommand]
    private async Task MoveDown() => await _owner.MoveAsync(Service, 1);

    [RelayCommand]
    private void Delete() => _owner.RequestDelete(Service);

    internal void NotifyOrderChanged()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Service.PropertyChanged -= OnServicePropertyChanged;
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ServiceInstance.DisplayName))
        {
            OnPropertyChanged(nameof(DisplayName));
        }
        else if (eventArgs.PropertyName is nameof(ServiceInstance.IsEnabled))
        {
            OnPropertyChanged(nameof(IsEnabled));
        }
        else if (eventArgs.PropertyName is nameof(ServiceInstance.IsMuted))
        {
            OnPropertyChanged(nameof(IsMuted));
        }
    }
}
