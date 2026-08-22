namespace UnifiedMessenger.App.Models;

public enum MailProviderType
{
    Gmail,
    Yandex,
    MailRu,
    GenericImap
}

public enum MailAuthenticationKind
{
    Password,
    OAuth
}

public enum MailSecureSocketMode
{
    SslOnConnect,
    StartTls
}
