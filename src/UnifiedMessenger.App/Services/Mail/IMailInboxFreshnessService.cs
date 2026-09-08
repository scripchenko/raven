namespace UnifiedMessenger.App.Services.Mail;

public interface IMailInboxFreshnessService
{
    void OnNewMailDetected(Guid mailAccountId, bool isAccountActivelyViewed);
    void RequireFreshInbox(Guid mailAccountId);
}
