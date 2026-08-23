namespace UnifiedMessenger.App.Services.Mail;

public enum MailCredentialKind
{
    Password = 1,
    GmailOAuthRefreshToken = 2
}

public sealed record MailCredential
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required MailCredentialKind Kind { get; init; }
    public required string Secret { get; init; }
    public string? OAuthClientId { get; init; }
    public string? OAuthClientSecret { get; init; }
    public string? OAuthScope { get; init; }

    public static MailCredential CreatePassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return new MailCredential
        {
            Kind = MailCredentialKind.Password,
            Secret = password
        };
    }

    public static MailCredential CreateGmailOAuth(
        string refreshToken,
        string clientId,
        string clientSecret,
        string oauthScope = GmailOAuthConstants.ReadOnlyScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        return new MailCredential
        {
            Kind = MailCredentialKind.GmailOAuthRefreshToken,
            Secret = refreshToken,
            OAuthClientId = clientId,
            OAuthClientSecret = clientSecret,
            OAuthScope = oauthScope
        };
    }

    public bool IsValid() =>
        SchemaVersion == CurrentSchemaVersion
        && !string.IsNullOrWhiteSpace(Secret)
        && Kind switch
        {
            MailCredentialKind.Password =>
                string.IsNullOrEmpty(OAuthClientId) && string.IsNullOrEmpty(OAuthClientSecret),
            MailCredentialKind.GmailOAuthRefreshToken =>
                !string.IsNullOrWhiteSpace(OAuthClientId)
                && !string.IsNullOrWhiteSpace(OAuthClientSecret)
                && (string.IsNullOrWhiteSpace(OAuthScope)
                    || OAuthScope is GmailOAuthConstants.ReadOnlyScope or GmailOAuthConstants.ModifyScope),
            _ => false
        };

    public bool HasGmailModifyScope =>
        Kind is MailCredentialKind.GmailOAuthRefreshToken
        && string.Equals(OAuthScope, GmailOAuthConstants.ModifyScope, StringComparison.Ordinal);
}
