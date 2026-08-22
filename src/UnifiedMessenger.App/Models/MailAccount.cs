using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UnifiedMessenger.App.Models;

public sealed partial class MailAccount : ObservableObject
{
    [ObservableProperty]
    private Guid _id;

    [ObservableProperty]
    private MailProviderType _provider;

    [ObservableProperty]
    private string _emailAddress = string.Empty;

    [ObservableProperty]
    private string? _displayName;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private string _credentialKey = string.Empty;

    [ObservableProperty]
    private MailAuthenticationKind _authenticationKind = MailAuthenticationKind.Password;

    [ObservableProperty]
    private DateTimeOffset? _lastSuccessfulConnectionUtc;

    [ObservableProperty]
    private MailConnectionSettings? _genericConnectionSettings;

    [ObservableProperty]
    private int _sortOrder;

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(DisplayName) ? EmailAddress : DisplayName;

    partial void OnDisplayNameChanged(string? value) => OnPropertyChanged(nameof(DisplayLabel));

    partial void OnEmailAddressChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));
}
