using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public abstract class PasswordMailProvider(IMailConnectionValidator validator) : IMailProvider
{
    protected IMailConnectionValidator Validator { get; } = validator;

    public abstract MailProviderType ProviderType { get; }
    public abstract MailProviderDescriptor Descriptor { get; }

    public async Task<MailConnectionValidationResult> ValidateAsync(
        MailAccountConnectionRequest request,
        string secret,
        CancellationToken cancellationToken = default)
    {
        if (request.Provider != ProviderType)
        {
            return MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.InvalidConfiguration,
                L.Instance.Get("Wrong mail provider selected."));
        }

        string email = request.EmailAddress.Trim();
        MailConnectionSettings? settings = CreateConnectionSettings(request, email);
        if (settings is null)
        {
            return MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.InvalidConfiguration,
                L.Instance.Get("Enter valid IMAP and SMTP settings."));
        }

        return await Validator.ValidateAsync(settings, email, secret, cancellationToken);
    }

    public abstract MailConnectionSettings? CreateConnectionSettings(
        MailAccountConnectionRequest request,
        string normalizedEmailAddress);

    protected static MailConnectionSettings CreatePreset(
        string imapHost,
        int imapPort,
        string smtpHost,
        int smtpPort,
        string username) =>
        new()
        {
            Imap = new MailServerSettings
            {
                Host = imapHost,
                Port = imapPort,
                SecureSocketMode = MailSecureSocketMode.SslOnConnect,
                Username = username
            },
            Smtp = new MailServerSettings
            {
                Host = smtpHost,
                Port = smtpPort,
                SecureSocketMode = MailSecureSocketMode.SslOnConnect,
                Username = username
            }
        };
}

public sealed class YandexMailProvider(IMailConnectionValidator validator) : PasswordMailProvider(validator)
{
    public override MailProviderType ProviderType => MailProviderType.Yandex;
    public override MailProviderDescriptor Descriptor { get; } = new(
        MailProviderType.Yandex,
        L.Instance.Get("Yandex Mail"),
        MailAuthenticationKind.Password,
        MailProviderCapabilities.ConnectionValidation
            | MailProviderCapabilities.IdentityValidation
            | MailProviderCapabilities.PasswordAuthentication
            | MailProviderCapabilities.MessageListing
            | MailProviderCapabilities.MessageReading
            | MailProviderCapabilities.Sending,
        L.Instance.Get("Use a Yandex app password, not your primary account password."));

    public override MailConnectionSettings CreateConnectionSettings(
        MailAccountConnectionRequest request,
        string normalizedEmailAddress) =>
        CreatePreset("imap.yandex.com", 993, "smtp.yandex.com", 465, normalizedEmailAddress);
}

public sealed class MailRuMailProvider(IMailConnectionValidator validator) : PasswordMailProvider(validator)
{
    public override MailProviderType ProviderType => MailProviderType.MailRu;
    public override MailProviderDescriptor Descriptor { get; } = new(
        MailProviderType.MailRu,
        L.Instance.Get("Mail.ru Mail"),
        MailAuthenticationKind.Password,
        MailProviderCapabilities.ConnectionValidation
            | MailProviderCapabilities.IdentityValidation
            | MailProviderCapabilities.PasswordAuthentication
            | MailProviderCapabilities.MessageListing
            | MailProviderCapabilities.MessageReading
            | MailProviderCapabilities.Sending,
        L.Instance.Get("Use a Mail.ru external-app password with full Mail access. IMAP/SMTP access must be enabled."));

    public override MailConnectionSettings CreateConnectionSettings(
        MailAccountConnectionRequest request,
        string normalizedEmailAddress) =>
        CreatePreset("imap.mail.ru", 993, "smtp.mail.ru", 465, normalizedEmailAddress);
}

public sealed class GenericImapMailProvider(IMailConnectionValidator validator) : PasswordMailProvider(validator)
{
    public override MailProviderType ProviderType => MailProviderType.GenericImap;
    public override MailProviderDescriptor Descriptor { get; } = new(
        MailProviderType.GenericImap,
        L.Instance.Get("Other mail (IMAP/SMTP)"),
        MailAuthenticationKind.Password,
        MailProviderCapabilities.ConnectionValidation
            | MailProviderCapabilities.IdentityValidation
            | MailProviderCapabilities.PasswordAuthentication
            | MailProviderCapabilities.MessageListing
            | MailProviderCapabilities.MessageReading
            | MailProviderCapabilities.Sending,
        L.Instance.Get("Enter the IMAP and SMTP settings from your mail provider."));

    public override MailConnectionSettings? CreateConnectionSettings(
        MailAccountConnectionRequest request,
        string normalizedEmailAddress)
    {
        MailConnectionSettings? settings = request.GenericConnectionSettings?.Clone();
        if (settings is null)
        {
            return null;
        }

        settings.Imap.Host = settings.Imap.Host.Trim();
        settings.Smtp.Host = settings.Smtp.Host.Trim();
        settings.Imap.Username = string.IsNullOrWhiteSpace(settings.Imap.Username)
            ? normalizedEmailAddress
            : settings.Imap.Username.Trim();
        settings.Smtp.Username = string.IsNullOrWhiteSpace(settings.Smtp.Username)
            ? normalizedEmailAddress
            : settings.Smtp.Username.Trim();
        return settings;
    }
}

public sealed class GmailApiProvider : IMailProvider
{
    public MailProviderType ProviderType => MailProviderType.Gmail;
    public MailProviderDescriptor Descriptor { get; } = new(
        MailProviderType.Gmail,
        "Gmail",
        MailAuthenticationKind.OAuth,
        MailProviderCapabilities.OAuthAuthentication
            | MailProviderCapabilities.IdentityValidation
            | MailProviderCapabilities.MessageListing
            | MailProviderCapabilities.MessageReading
            | MailProviderCapabilities.Sending,
        L.Instance.Get("Authorization opens in the system browser. Your Google password is not entered in raven."));

    public Task<MailConnectionValidationResult> ValidateAsync(
        MailAccountConnectionRequest request,
        string secret,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            MailConnectionValidationResult.Failure(
                MailConnectionFailureKind.OAuthNotAvailable,
                L.Instance.Get("For Gmail, sign in to Google in the system browser.")));
}

public sealed class MailProviderFactory(IEnumerable<IMailProvider> providers) : IMailProviderFactory
{
    private readonly IReadOnlyDictionary<MailProviderType, IMailProvider> _providers = providers
        .ToDictionary(provider => provider.ProviderType);

    public IReadOnlyList<MailProviderDescriptor> Providers => _providers.Values
        .Select(provider => provider.Descriptor)
        .OrderBy(descriptor => descriptor.Provider)
        .ToList();

    public IMailProvider Get(MailProviderType providerType) =>
        _providers.TryGetValue(providerType, out IMailProvider? provider)
            ? provider
            : throw new KeyNotFoundException($"Mail provider '{providerType}' is not registered.");
}
