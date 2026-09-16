using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public enum NotificationPopupBrand
{
    Default,
    Gmail,
    Yandex
}

public sealed record NotificationPopupDisplayModel(
    Guid NotificationId,
    Guid ServiceInstanceId,
    string ServiceName,
    string Title,
    string Body,
    string? PreviewText = null,
    NotificationPopupBrand Brand = NotificationPopupBrand.Default,
    string? SenderAvatarInitials = null,
    string? SenderAddress = null)
{
    public bool IsGmail => Brand is NotificationPopupBrand.Gmail;
    public bool IsYandex => Brand is NotificationPopupBrand.Yandex;
    public bool HasPreviewText => !string.IsNullOrWhiteSpace(PreviewText);
    public double PreviewMaxHeight => IsYandex ? 30d : 48d;
    public bool ShowSenderInitials => (IsGmail || IsYandex) && !string.IsNullOrWhiteSpace(SenderAvatarInitials);
    public bool ShowContentSourceIcon => !ShowSenderInitials;
    public string SenderAvatarBackground =>
        MailMessageSummary.GetSenderAvatarBackground(Title, SenderAddress ?? string.Empty);
}
