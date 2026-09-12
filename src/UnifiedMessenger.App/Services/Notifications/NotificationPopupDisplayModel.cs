namespace UnifiedMessenger.App.Services.Notifications;

public enum NotificationPopupBrand
{
    Default,
    Gmail
}

public sealed record NotificationPopupDisplayModel(
    Guid NotificationId,
    Guid ServiceInstanceId,
    string ServiceName,
    string Title,
    string Body,
    string? PreviewText = null,
    NotificationPopupBrand Brand = NotificationPopupBrand.Default,
    string? SenderAvatarInitials = null)
{
    public bool IsGmail => Brand is NotificationPopupBrand.Gmail;
    public bool HasPreviewText => !string.IsNullOrWhiteSpace(PreviewText);
    public bool ShowSenderInitials => IsGmail && !string.IsNullOrWhiteSpace(SenderAvatarInitials);
    public bool ShowContentSourceIcon => !ShowSenderInitials;
}
