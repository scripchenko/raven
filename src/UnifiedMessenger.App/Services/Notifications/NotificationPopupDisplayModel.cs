using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Notifications;

public enum NotificationPopupBrand
{
    Default,
    Telegram,
    WhatsApp,
    Gmail,
    Yandex,
    MailRu
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
    string? SenderAddress = null,
    string? SenderAvatarIdentity = null)
{
    public bool IsTelegram => Brand is NotificationPopupBrand.Telegram;
    public bool IsWhatsApp => Brand is NotificationPopupBrand.WhatsApp;
    public bool IsGmail => Brand is NotificationPopupBrand.Gmail;
    public bool IsYandex => Brand is NotificationPopupBrand.Yandex;
    public bool IsMailRu => Brand is NotificationPopupBrand.MailRu;
    public bool IsMessenger => IsTelegram || IsWhatsApp;
    public bool HasPreviewText => !string.IsNullOrWhiteSpace(PreviewText);
    public double PreviewMaxHeight => IsYandex || IsMailRu ? 30d : 48d;
    public bool ShowSenderInitials => (IsMessenger || IsGmail || IsYandex || IsMailRu)
        && !string.IsNullOrWhiteSpace(SenderAvatarInitials);
    public bool ShowContentSourceIcon => !ShowSenderInitials;
    public string SenderAvatarBackground =>
        MailMessageSummary.GetSenderAvatarBackground(
            Title,
            SenderAvatarIdentity ?? SenderAddress ?? string.Empty);
}
