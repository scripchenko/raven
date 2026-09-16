using System.Text;
using MimeKit;
using MimeKit.Utils;
using UnifiedMessenger.App.Models;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailComposeRequestFactory : IMailComposeRequestFactory
{
    public MailComposeRequest Create(MailAccount account, MailComposeInput input) =>
        CreateCore(account, input, requireRecipient: true);

    public MailComposeRequest CreateDraft(MailAccount account, MailComposeInput input) =>
        CreateCore(account, input, requireRecipient: false);

    private static MailComposeRequest CreateCore(
        MailAccount account,
        MailComposeInput input,
        bool requireRecipient)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(input);
        if (account.Id == Guid.Empty || !account.IsEnabled)
        {
            throw new MailComposeValidationException("Текущий почтовый аккаунт недоступен для отправки.");
        }

        RejectHeaderBreaks(input.Subject, "Тема содержит недопустимые переводы строк.");
        IReadOnlyList<MailAddress> to = ParseAddresses(input.To, "Проверьте адреса в поле «Кому».");
        IReadOnlyList<MailAddress> cc = ParseAddresses(input.Cc, "Проверьте адреса в поле «Копия».");
        IReadOnlyList<MailAddress> bcc = ParseAddresses(input.Bcc, "Проверьте адреса в поле «Скрытая копия».");
        if (requireRecipient && to.Count + cc.Count + bcc.Count == 0)
        {
            throw new MailComposeValidationException("Укажите хотя бы одного получателя.");
        }

        return new MailComposeRequest(
            account.Id,
            to,
            cc,
            bcc,
            input.Subject.Trim(),
            NormalizeBody(input.TextBody),
            input.ReplyContext)
        {
            Attachments = input.Attachments.ToArray()
        };
    }

    internal static IReadOnlyList<MailAddress> ParseAddresses(string? value, string failureMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        RejectHeaderBreaks(value, failureMessage);
        try
        {
            InternetAddressList parsed = InternetAddressList.Parse(value);
            MailAddress[] addresses = parsed.Mailboxes
                .Select(mailbox => new MailAddress(
                    mailbox.Name?.Trim() ?? string.Empty,
                    mailbox.Address?.Trim() ?? string.Empty))
                .ToArray();
            if (addresses.Length == 0 || addresses.Any(address => !IsValidMailboxAddress(address.Address)))
            {
                throw new MailComposeValidationException(failureMessage);
            }

            return addresses;
        }
        catch (ParseException)
        {
            throw new MailComposeValidationException(failureMessage);
        }
        catch (ArgumentException)
        {
            throw new MailComposeValidationException(failureMessage);
        }
    }

    internal static bool IsValidMailboxAddress(string? address)
    {
        string normalized = address?.Trim() ?? string.Empty;
        int separator = normalized.LastIndexOf('@');
        if (separator <= 0
            || separator == normalized.Length - 1
            || normalized[..separator].Any(char.IsControl)
            || normalized[(separator + 1)..].Any(char.IsControl))
        {
            return false;
        }

        return Uri.CheckHostName(normalized[(separator + 1)..]) is UriHostNameType.Dns;
    }

    private static void RejectHeaderBreaks(string? value, string failureMessage)
    {
        if (value?.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new MailComposeValidationException(failureMessage);
        }
    }

    private static string NormalizeBody(string? body) =>
        (body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

internal sealed class MailMimeMessageFactory(TimeProvider timeProvider) : IMailMimeMessageFactory
{
    public MailMimeSubmission Create(
        MailAccount account,
        MailComposeRequest request,
        IReadOnlyList<MaterializedMailAttachment>? attachments = null) =>
        CreateCore(account, request, attachments, requireRecipient: true);

    public MailMimeSubmission CreateDraft(
        MailAccount account,
        MailComposeRequest request,
        IReadOnlyList<MaterializedMailAttachment>? attachments = null) =>
        CreateCore(account, request, attachments, requireRecipient: false);

    private MailMimeSubmission CreateCore(
        MailAccount account,
        MailComposeRequest request,
        IReadOnlyList<MaterializedMailAttachment>? attachments,
        bool requireRecipient)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(request);
        if (account.Id != request.AccountId || (requireRecipient && request.RecipientCount == 0))
        {
            throw new MailComposeValidationException("Параметры отправки письма некорректны.");
        }

        MailProviderFeaturePolicy providerPolicy = MailProviderFeaturePolicies.Get(account.Provider);
        string? senderDisplayName = providerPolicy.IncludeAccountDisplayNameInFromHeader
            ? account.DisplayName
            : null;
        MailboxAddress sender = CreateMailbox(senderDisplayName, account.EmailAddress);
        MailboxAddress[] to = request.To.Select(CreateMailbox).ToArray();
        MailboxAddress[] cc = request.Cc.Select(CreateMailbox).ToArray();
        MailboxAddress[] bcc = request.Bcc.Select(CreateMailbox).ToArray();
        MaterializedMailAttachment[] materialized = attachments?.ToArray() ?? [];
        MimeMessage message = new()
        {
            Date = timeProvider.GetUtcNow(),
            MessageId = MimeUtils.GenerateMessageId(),
            Subject = request.Subject
        };
        message.Body = CreateBody(request.TextBody, materialized);
        message.From.Add(sender);
        message.To.AddRange(to);
        message.Cc.AddRange(cc);
        message.Bcc.AddRange(bcc);
        ApplyReplyContext(message, request.ReplyContext);

        return new MailMimeSubmission(message, sender, [.. to, .. cc, .. bcc]);
    }

    private static MimeEntity CreateBody(
        string textBody,
        IReadOnlyList<MaterializedMailAttachment> attachments)
    {
        TextPart text = new("plain")
        {
            Text = textBody,
            ContentTransferEncoding = ContentEncoding.QuotedPrintable
        };
        text.ContentType.Charset = "utf-8";
        if (attachments.Count == 0)
        {
            return text;
        }

        BodyBuilder builder = new()
        {
            TextBody = textBody,
            BodyEncoding = Encoding.UTF8
        };
        foreach (MaterializedMailAttachment attachment in attachments)
        {
            ContentType contentType;
            try
            {
                contentType = ContentType.Parse(attachment.ContentType);
            }
            catch (ParseException)
            {
                contentType = new ContentType("application", "octet-stream");
            }

            MimeEntity entity = builder.Attachments.Add(
                MailAttachmentFileName.Sanitize(attachment.FileName),
                attachment.Bytes.ToArray(),
                contentType);
            entity.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment);
        }

        return builder.ToMessageBody();
    }

    private static void ApplyReplyContext(MimeMessage message, MailReplyContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.InReplyTo))
        {
            message.InReplyTo = context.InReplyTo;
        }

        foreach (string reference in context.References.Where(reference => !string.IsNullOrWhiteSpace(reference)))
        {
            if (!message.References.Contains(reference))
            {
                message.References.Add(reference);
            }
        }
    }

    private static MailboxAddress CreateMailbox(MailAddress address) =>
        CreateMailbox(address.DisplayName, address.Address);

    private static MailboxAddress CreateMailbox(string? displayName, string address) =>
        new(displayName?.Trim() ?? string.Empty, address.Trim());
}

public sealed class MailSendProviderFactory(IEnumerable<IMailSendProvider> providers) : IMailSendProviderFactory
{
    private readonly IReadOnlyList<IMailSendProvider> _providers = providers.ToArray();

    public IMailSendProvider Get(MailProviderType providerType)
    {
        IMailSendProvider[] matches = _providers.Where(provider => provider.Supports(providerType)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new KeyNotFoundException(
                $"Mail send provider for '{providerType}' is not registered uniquely.");
    }
}
