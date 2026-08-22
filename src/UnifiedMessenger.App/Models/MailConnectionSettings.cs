namespace UnifiedMessenger.App.Models;

public sealed class MailConnectionSettings
{
    public MailServerSettings Imap { get; set; } = MailServerSettings.CreateDefaultImap();
    public MailServerSettings Smtp { get; set; } = MailServerSettings.CreateDefaultSmtp();

    public MailConnectionSettings Clone() =>
        new()
        {
            Imap = Imap.Clone(),
            Smtp = Smtp.Clone()
        };
}

public sealed class MailServerSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public MailSecureSocketMode SecureSocketMode { get; set; } = MailSecureSocketMode.SslOnConnect;
    public string Username { get; set; } = string.Empty;

    public MailServerSettings Clone() =>
        new()
        {
            Host = Host,
            Port = Port,
            SecureSocketMode = SecureSocketMode,
            Username = Username
        };

    public static MailServerSettings CreateDefaultImap() =>
        new() { Port = 993, SecureSocketMode = MailSecureSocketMode.SslOnConnect };

    public static MailServerSettings CreateDefaultSmtp() =>
        new() { Port = 465, SecureSocketMode = MailSecureSocketMode.SslOnConnect };
}
