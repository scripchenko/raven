using System.Xml.Linq;
using Google.Apis.Gmail.v1.Data;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class MailListAttachmentMetadataTests
{
    [Fact]
    public void SummaryWithoutAttachments_HasNoPaperclipState()
    {
        MailMessageSummary summary = Summary("none");

        Assert.False(summary.HasAttachments);
        Assert.Equal(0, summary.AttachmentCount);
        Assert.Empty(summary.AttachmentPreviewItems);
        Assert.False(summary.HasMoreAttachments);
    }

    [Fact]
    public void OneAttachment_ExposesOneSafePreview()
    {
        MailMessageSummary summary = WithAttachments(
            Summary("one"),
            Attachment("invoice.pdf", "application/pdf", 42));

        MailAttachmentPreviewItem preview = Assert.Single(summary.AttachmentPreviewItems);
        Assert.True(summary.HasAttachments);
        Assert.Equal(1, summary.AttachmentCount);
        Assert.Equal("invoice.pdf", preview.DisplayFileName);
        Assert.Equal("PDF", preview.TypeLabel);
        Assert.False(summary.HasMoreAttachments);
    }

    [Fact]
    public void TwoAttachments_ExposeBothPreviews()
    {
        MailMessageSummary summary = WithAttachments(
            Summary("two"),
            Attachment("invoice.pdf"),
            Attachment("photo.jpg", "image/jpeg"));

        Assert.Equal(2, summary.AttachmentCount);
        Assert.Equal(["invoice.pdf", "photo.jpg"], summary.AttachmentPreviewItems.Select(item => item.DisplayFileName));
        Assert.False(summary.HasMoreAttachments);
    }

    [Fact]
    public void MoreThanTwoAttachments_UsesTwoPreviewsAndRemainingCount()
    {
        MailMessageSummary summary = WithAttachments(
            Summary("many"),
            Attachment("one.pdf"),
            Attachment("two.pdf"),
            Attachment("three.pdf"),
            Attachment("four.pdf"));

        Assert.Equal(4, summary.AttachmentCount);
        Assert.Equal(2, summary.AttachmentPreviewItems.Count);
        Assert.True(summary.HasMoreAttachments);
        Assert.Equal("+2", summary.RemainingAttachmentText);
    }

    [Fact]
    public void DuplicateFileNames_RemainDistinctAttachments()
    {
        MailMessageSummary summary = WithAttachments(
            Summary("duplicates"),
            Attachment("document.pdf"),
            Attachment("document.pdf"));

        Assert.Equal(2, summary.AttachmentCount);
        Assert.Equal(2, summary.AttachmentPreviewItems.Count);
    }

    [Fact]
    public void ZeroByteAttachment_IsStillCounted()
    {
        MailMessageSummary summary = WithAttachments(
            Summary("zero"),
            Attachment("empty.txt", size: 0));

        MailAttachmentPreviewItem preview = Assert.Single(summary.AttachmentPreviewItems);
        Assert.Equal(0, preview.Size);
        Assert.True(summary.HasAttachments);
    }

    [Theory]
    [InlineData("application/pdf", MailAttachmentVisualType.Pdf)]
    [InlineData("image/png", MailAttachmentVisualType.Image)]
    [InlineData("image/jpeg", MailAttachmentVisualType.Image)]
    [InlineData("text/plain", MailAttachmentVisualType.Text)]
    [InlineData("application/msword", MailAttachmentVisualType.Document)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", MailAttachmentVisualType.Document)]
    [InlineData("application/vnd.ms-excel", MailAttachmentVisualType.Spreadsheet)]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", MailAttachmentVisualType.Spreadsheet)]
    [InlineData("text/csv", MailAttachmentVisualType.Spreadsheet)]
    [InlineData("application/zip", MailAttachmentVisualType.Archive)]
    [InlineData("application/x-7z-compressed", MailAttachmentVisualType.Archive)]
    [InlineData("application/octet-stream", MailAttachmentVisualType.Generic)]
    [InlineData("application/x-unknown", MailAttachmentVisualType.Generic)]
    public void MimeType_MapsToProviderNeutralVisualCategory(
        string contentType,
        MailAttachmentVisualType expected)
    {
        Assert.Equal(expected, MailAttachmentVisualCatalog.Resolve(contentType));
    }

    [Fact]
    public void MimeParameters_DoNotChangeVisualCategory()
    {
        Assert.Equal(
            MailAttachmentVisualType.Pdf,
            MailAttachmentVisualCatalog.Resolve("application/pdf; name=invoice.pdf"));
    }

    [Fact]
    public void GmailCidOnlyPart_IsNotAnAttachment()
    {
        MessagePart payload = Multipart(
            Part("logo.png", "image/png", "inline", "<logo@example.test>"));

        MailMessageAttachmentSummary result = GmailApiReadClient.GetAttachmentSummary(payload);

        Assert.Equal(0, result.Count);
        Assert.Empty(result.PreviewItems);
    }

    [Fact]
    public void GmailCidAndPdf_CountsOnlyTheRealPdfAttachment()
    {
        MessagePart payload = Multipart(
            Part("logo.png", "image/png", "inline", "<logo@example.test>"),
            Part("invoice.pdf", "application/pdf", "attachment"));

        MailMessageAttachmentSummary result = GmailApiReadClient.GetAttachmentSummary(payload);

        MailAttachmentPreviewItem preview = Assert.Single(result.PreviewItems);
        Assert.Equal(1, result.Count);
        Assert.Equal("invoice.pdf", preview.DisplayFileName);
    }

    [Fact]
    public void GmailSummaryProjection_ContainsMetadataButNoRawOrAttachmentBytes()
    {
        string fields = GmailApiReadClient.MetadataFieldsProjection;

        Assert.Contains("filename", fields, StringComparison.Ordinal);
        Assert.Contains("mimeType", fields, StringComparison.Ordinal);
        Assert.Contains("body/size", fields, StringComparison.Ordinal);
        Assert.Contains("parts", fields, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body/data", fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attachmentId", fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partId", fields, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(GmailApiReadClient.MaximumMetadataConcurrency, 1, 5);
    }

    [Fact]
    public void GmailAttachmentMetadata_MapsToProviderNeutralSummary()
    {
        GmailApiSummaryData providerSummary = new(
            "provider-message-key",
            "Subject",
            "Sender <sender@example.test>",
            1_700_000_000_000,
            "Preview",
            ["INBOX"])
        {
            AttachmentSummary = MailMessageAttachmentSummary.Create(
                [Attachment("invoice.pdf", "application/pdf", 64)])
        };

        MailMessageSummary result = GmailMailReadProvider.MapSummary(providerSummary);

        Assert.True(result.HasAttachments);
        Assert.Equal("invoice.pdf", Assert.Single(result.AttachmentPreviewItems).DisplayFileName);
    }

    [Fact]
    public void ImapAttachmentMetadata_MapsToProviderNeutralSummary()
    {
        ImapSummaryData providerSummary = new(
            42,
            "Subject",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            false)
        {
            AttachmentSummary = MailMessageAttachmentSummary.Create(
                [Attachment("archive.zip", "application/zip", 256)])
        };

        MailMessageSummary result = ImapMailReadProvider.MapSummary(providerSummary);

        Assert.True(result.HasAttachments);
        Assert.Equal("archive.zip", Assert.Single(result.AttachmentPreviewItems).DisplayFileName);
    }

    [Fact]
    public void ImapListFetch_UsesBodyStructureAndDoesNotDownloadMessageBody()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Mail", "ImapMailReadProvider.cs"));
        int start = source.IndexOf(
            "public async Task<ImapInboxPageData> GetFolderPageAsync",
            StringComparison.Ordinal);
        int end = source.IndexOf(
            "public async Task<ImapMessageData> GetMessageAsync",
            start,
            StringComparison.Ordinal);
        string listFetch = source[start..end];

        Assert.Contains("MessageSummaryItems.BodyStructure", listFetch, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMessageAsync", listFetch, StringComparison.Ordinal);
        Assert.DoesNotContain("MimeMessage.Load", listFetch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_ReplacesAttachmentMetadataForTheSameStableMessage()
    {
        MailMessageSummary initial = Summary("stable-message");
        MailMessageSummary refreshed = WithAttachments(
            initial,
            Attachment("new-document.pdf", "application/pdf", 128));
        QueueProvider provider = new(
            new MailPage<MailMessageSummary>([initial], null),
            new MailPage<MailMessageSummary>([refreshed], null));
        using MailInboxViewModel viewModel = new(new SingleProviderFactory(provider));

        await viewModel.ActivateAsync(Account());
        await viewModel.RefreshCommand.ExecuteAsync(null);

        MailMessageSummary result = Assert.Single(viewModel.Messages);
        Assert.Equal("stable-message", result.MessageKey);
        Assert.True(result.HasAttachments);
        Assert.Equal("new-document.pdf", Assert.Single(result.AttachmentPreviewItems).DisplayFileName);
    }

    [Fact]
    public void AttachmentSummaryContract_ContainsNoProviderSpecificIdentifiers()
    {
        Type[] contractTypes =
        [
            typeof(MailMessageSummary),
            typeof(MailMessageAttachmentSummary),
            typeof(MailAttachmentPreviewItem)
        ];

        foreach (Type type in contractTypes)
        {
            Assert.DoesNotContain(
                type.GetProperties(),
                property => property.Name.Contains("AttachmentId", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("PartId", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("ProviderId", StringComparison.OrdinalIgnoreCase)
                    || property.PropertyType.Namespace?.StartsWith("Google", StringComparison.Ordinal) == true
                    || property.PropertyType.Namespace?.StartsWith("MailKit", StringComparison.Ordinal) == true
                    || property.PropertyType.Namespace?.StartsWith("MimeKit", StringComparison.Ordinal) == true);
        }

        Assert.DoesNotContain(
            typeof(MailInboxViewModel).GetProperties(),
            property => property.Name.Contains("AttachmentId", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("PartId", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("ProviderId", StringComparison.OrdinalIgnoreCase)
                || property.PropertyType.Namespace?.StartsWith("Google", StringComparison.Ordinal) == true
                || property.PropertyType.Namespace?.StartsWith("MailKit", StringComparison.Ordinal) == true
                || property.PropertyType.Namespace?.StartsWith("MimeKit", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void MailListTemplate_ShowsTypedPreviewChipsAndRemainingCount()
    {
        XDocument view = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        string xaml = view.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("AttachmentPreviewItems", xaml, StringComparison.Ordinal);
        Assert.Contains("VisualType", xaml, StringComparison.Ordinal);
        Assert.Contains("TypeLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("HasMoreAttachments", xaml, StringComparison.Ordinal);
        Assert.Contains("RemainingAttachmentText", xaml, StringComparison.Ordinal);
        Assert.Contains("DisplayFileName", xaml, StringComparison.Ordinal);
        Assert.Equal(2, MailMessageAttachmentSummary.MaximumPreviewItems);
    }

    private static MailMessageSummary Summary(string key) =>
        new(
            key,
            "Subject",
            "Sender",
            "sender@example.test",
            DateTimeOffset.UtcNow,
            "Preview",
            false);

    private static MailMessageSummary WithAttachments(
        MailMessageSummary summary,
        params MailAttachmentPreviewItem[] attachments) =>
        summary with { AttachmentSummary = MailMessageAttachmentSummary.Create(attachments) };

    private static MailAttachmentPreviewItem Attachment(
        string fileName,
        string contentType = "application/octet-stream",
        long? size = null) =>
        new(fileName, contentType, size);

    private static MessagePart Multipart(params MessagePart[] parts) =>
        new()
        {
            MimeType = "multipart/mixed",
            Parts = parts.ToList()
        };

    private static MessagePart Part(
        string fileName,
        string contentType,
        string disposition,
        string? contentId = null)
    {
        List<MessagePartHeader> headers =
        [
            new() { Name = "Content-Disposition", Value = disposition }
        ];
        if (contentId is not null)
        {
            headers.Add(new MessagePartHeader { Name = "Content-ID", Value = contentId });
        }

        return new MessagePart
        {
            Filename = fileName,
            MimeType = contentType,
            Body = new MessagePartBody { Size = 0 },
            Headers = headers
        };
    }

    private static MailAccount Account() =>
        new()
        {
            Id = Guid.NewGuid(),
            Provider = MailProviderType.Gmail,
            EmailAddress = "account@example.test",
            CredentialKey = Guid.NewGuid().ToString("N"),
            AuthenticationKind = MailAuthenticationKind.OAuth,
            IsEnabled = true
        };

    private static string FindRepositoryFile(params string[] relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(Path.Combine(relativePath));
    }

    private sealed class SingleProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class QueueProvider(params MailPage<MailMessageSummary>[] pages) : IMailReadProvider
    {
        private readonly Queue<MailPage<MailMessageSummary>> _pages = new(pages);

        public bool Supports(MailProviderType providerType) => true;

        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(
            MailAccount account,
            string? continuationToken,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_pages.Dequeue());

        public Task<MailMessageContent> GetMessageAsync(
            MailAccount account,
            string messageKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refresh of list metadata must not download a message body.");
    }
}
