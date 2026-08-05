using System.Windows;
using System.Windows.Controls;
using UnifiedMessenger.App.Models;
using WpfMessageBox = System.Windows.MessageBox;

namespace UnifiedMessenger.App.Views;

public partial class AddServiceWindow : Window
{
    private string? _lastDefaultName;

    public AddServiceWindow(IReadOnlyList<ServiceDefinition> availableServices)
    {
        ArgumentNullException.ThrowIfNull(availableServices);
        InitializeComponent();
        ServiceList.ItemsSource = availableServices;
        ServiceList.SelectedIndex = availableServices.Count > 0 ? 0 : -1;
    }

    public ServiceType SelectedServiceType { get; private set; }
    public string AccountName { get; private set; } = string.Empty;

    private void ServiceList_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (ServiceList.SelectedItem is not ServiceDefinition definition)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(AccountNameTextBox.Text)
            || string.Equals(AccountNameTextBox.Text, _lastDefaultName, StringComparison.Ordinal))
        {
            AccountNameTextBox.Text = definition.DisplayName;
            AccountNameTextBox.SelectAll();
        }

        _lastDefaultName = definition.DisplayName;
    }

    private void Add_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (ServiceList.SelectedItem is not ServiceDefinition definition)
        {
            WpfMessageBox.Show(this, "Выберите сервис.", "Добавить сервис", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string accountName = AccountNameTextBox.Text.Trim();
        if (accountName.Length is 0 or > 80)
        {
            WpfMessageBox.Show(
                this,
                "Название аккаунта должно содержать от 1 до 80 символов.",
                "Некорректное название",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            AccountNameTextBox.Focus();
            return;
        }

        SelectedServiceType = definition.ServiceType;
        AccountName = accountName;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs eventArgs) => DialogResult = false;
}
