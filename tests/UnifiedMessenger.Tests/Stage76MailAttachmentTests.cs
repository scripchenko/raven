using System.Text;
using MailKit;
using MimeKit;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;
using UnifiedMessenger.App.ViewModels;

namespace UnifiedMessenger.Tests;

public sealed class Stage76MailAttachmentTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Incoming_PlainAttachmentExposesNameSizeAndContentType()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("attachment-content");
        MailMessageContent content = Extract(MessageWithAttachments(("report.pdf", "application/pdf", bytes)));

        MailAttachmentInfo attachment = Assert.Single(content.Attachments);
        Assert.True(content.HasAttachments);
        Assert.Equal("report.pdf", attachment.FileName);
        Assert.Equal(bytes.Length, attachment.Size);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.True(attachment.IsDownloadable);
        Assert.False(attachment.IsInline);
        Assert.False(string.IsNullOrWhiteSpace(attachment.AttachmentKey));
        Assert.Contains("Б", attachment.DisplaySize, StringComparison.Ordinal);
    }

    [Fact]
    public void Incoming_UnicodeFilenameIsPreserved()
    {
        MailMessageContent content = Extract(MessageWithAttachments(("отчёт №1.pdf", "application/pdf", [1, 2])));
        Assert.Equal("отчёт №1.pdf", Assert.Single(content.Attachments).FileName);
    }

    [Fact]
    public void Incoming_MissingFilenameUsesNeutralSafeName()
    {
        MimePart part = new("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream([1, 2, 3], writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
        };
        MimeMessage message = BasicMessage();
        message.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Body" }, part };

        Assert.Equal("attachment.bin", Assert.Single(Extract(message).Attachments).FileName);
    }

    [Fact]
    public void Incoming_HtmlCidLogoAndPdfExposeOnlyPdf()
    {
        BodyBuilder builder = new()
        {
            HtmlBody = "<html><body><img src=\"cid:logo-id\"><p>Hello</p></body></html>",
            TextBody = "Hello"
        };
        MimeEntity logo = builder.LinkedResources.Add(
            "logo.png",
            [9, 8, 7],
            new ContentType("image", "png"));
        logo.ContentId = "logo-id";
        builder.Attachments.Add("document.pdf", [1, 2, 3, 4], new ContentType("application", "pdf"));
        MimeMessage message = BasicMessage();
        message.Body = builder.ToMessageBody();

        MailAttachmentInfo attachment = Assert.Single(Extract(message).Attachments);
        Assert.Equal("document.pdf", attachment.FileName);
    }

    [Fact]
    public void Incoming_TechnicalBodyPartsAreNotAttachments()
    {
        MimeMessage message = BasicMessage();
        BodyBuilder builder = new() { TextBody = "Plain", HtmlBody = "<p>HTML</p>" };
        message.Body = builder.ToMessageBody();

        Assert.Empty(Extract(message).Attachments);
        Assert.False(Extract(message).HasAttachments);
    }

    [Fact]
    public void Incoming_DuplicateFilenamesRemainDistinctByAttachmentKey()
    {
        MailMessageContent content = Extract(MessageWithAttachments(
            ("report.pdf", "application/pdf", [1]),
            ("report.pdf", "application/pdf", [2])));

        Assert.Equal(2, content.Attachments.Count);
        Assert.Equal(2, content.Attachments.Select(item => item.AttachmentKey).Distinct().Count());
        Assert.All(content.Attachments, item => Assert.Equal("report.pdf", item.FileName));
    }

    [Fact]
    public void Incoming_ZeroByteAttachmentIsSupported()
    {
        MailAttachmentInfo attachment = Assert.Single(
            Extract(MessageWithAttachments(("empty.txt", "text/plain", []))).Attachments);
        Assert.Equal(0, attachment.Size);
    }

    [Theory]
    [InlineData("../../secret.txt", "secret.txt")]
    [InlineData("..\\..\\secret.txt", "secret.txt")]
    [InlineData("C:\\private\\secret.txt", "secret.txt")]
    [InlineData("\\\\server\\share\\secret.txt", "secret.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a<b>.txt", "a_b_.txt")]
    [InlineData("файл.txt", "файл.txt")]
    public void FilenameSanitizer_RemovesPathAndReservedSemantics(string input, string expected) =>
        Assert.Equal(expected, MailAttachmentFileName.Sanitize(input));

    [Fact]
    public async Task Save_ExplicitActionWritesExactBytesAndRemovesTemporaryFile()
    {
        using TemporaryDirectory directory = new();
        string destination = Path.Combine(directory.Path, "saved.bin");
        byte[] bytes = [0, 1, 2, 255];
        RecordingAttachmentProvider provider = new(bytes);
        MailAttachmentSaveService service = new(
            new FixedDialogService(destination),
            new MailAttachmentContentProviderFactory([provider]));

        MailAttachmentSaveResult result = await service.SaveAsync(
            Account(MailProviderType.Gmail),
            "gmail:message",
            Info("mime:0.1", "source.bin", bytes.Length));

        Assert.Equal(MailAttachmentSaveOutcome.Saved, result.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.um-part-*"));
        Assert.Equal(1, provider.GetCount);
    }

    [Fact]
    public async Task Save_CancelWritesNothingAndDoesNotFetchContent()
    {
        using TemporaryDirectory directory = new();
        RecordingAttachmentProvider provider = new([1]);
        MailAttachmentSaveService service = new(
            new FixedDialogService(null),
            new MailAttachmentContentProviderFactory([provider]));

        MailAttachmentSaveResult result = await service.SaveAsync(
            Account(MailProviderType.Gmail), "gmail:message", Info("mime:0", "a.bin", 1));

        Assert.Equal(MailAttachmentSaveOutcome.Canceled, result.Outcome);
        Assert.Equal(0, provider.GetCount);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Save_MoveFailureRemovesPartialFile()
    {
        using TemporaryDirectory directory = new();
        string destinationIsDirectory = directory.Path;
        MailAttachmentSaveService service = new(
            new FixedDialogService(destinationIsDirectory),
            new MailAttachmentContentProviderFactory([new RecordingAttachmentProvider([1, 2, 3])]));

        MailAttachmentSaveResult result = await service.SaveAsync(
            Account(MailProviderType.Gmail), "gmail:message", Info("mime:0", "a.bin", 3));

        Assert.Equal(MailAttachmentSaveOutcome.Failed, result.Outcome);
        Assert.Empty(Directory.GetFiles(directory.ParentPath, directory.Name + ".um-part-*"));
    }

    [Fact]
    public async Task StaleSaveCannotPublishStatusAfterMessageSelectionChanges()
    {
        MailAttachmentInfo attachment = Info("mime:0.1", "file.bin", 1);
        MailMessageContent content = ContentWithAttachments([attachment]);
        SingleMessageReadProvider read = new(content);
        DeferredSaveService save = new();
        using MailInboxViewModel inbox = new(
            new FixedReadProviderFactory(read),
            composeViewModel: null,
            save,
            new MailMessageSourceCache());
        await inbox.ActivateAsync(Account(MailProviderType.Gmail));
        inbox.SelectedMessageSummary = Assert.Single(inbox.Messages);
        await inbox.CurrentMessageLoadTask;

        Task operation = inbox.SaveAttachmentCommand.ExecuteAsync(attachment);
        await save.Started.Task;
        inbox.SelectedMessageSummary = null;
        save.Completion.TrySetResult(new MailAttachmentSaveResult(MailAttachmentSaveOutcome.Saved, "Файл сохранён"));
        await operation;

        Assert.True(save.WasCanceled);
        Assert.Null(inbox.AttachmentStatusMessage);
    }

    [Fact]
    public void OpeningMessageDoesNotWriteFilesOrInvokeAttachmentProvider()
    {
        using TemporaryDirectory directory = new();
        RecordingAttachmentProvider provider = new([1]);
        _ = Extract(MessageWithAttachments(("file.bin", "application/octet-stream", [1])));

        Assert.Equal(0, provider.GetCount);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task GmailReadProvider_ReusesBoundedSourceCacheForImmediateSave()
    {
        MimeMessage source = MessageWithAttachments(("cached.bin", "application/octet-stream", [4, 5, 6]));
        byte[] raw = SerializeBytes(source);
        RecordingGmailReadClient client = new(raw);
        GmailMailReadProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            client,
            new MailContentExtractor(new MailHtmlSanitizer()),
            new MailMessageSourceCache());
        MailAccount account = Account(MailProviderType.Gmail);

        MailMessageContent content = await provider.GetMessageAsync(account, "gmail:message");
        MailAttachmentContent attachment = await provider.GetAsync(
            account,
            content.MessageKey,
            Assert.Single(content.Attachments).AttachmentKey);

        Assert.Equal([4, 5, 6], attachment.Bytes.ToArray());
        Assert.Equal(1, client.RawMessageCount);
    }

    [Fact]
    public void SourceCache_IsBoundedByCountAndCanBeClearedPerAccount()
    {
        MailMessageSourceCache cache = new();
        Guid accountId = Guid.NewGuid();
        for (int index = 0; index < MailAttachmentLimits.SourceCacheCapacity + 2; index++)
        {
            cache.Set(accountId, $"message-{index}", BasicMessage(), 100);
        }

        Assert.Equal(MailAttachmentLimits.SourceCacheCapacity, cache.Count);
        cache.RemoveAccount(accountId);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void LocalCompose_AddMultipleRemoveOneAndHideFullPaths()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        string firstPath = first.CreateFile("same.txt", [1]);
        string secondPath = second.CreateFile("same.txt", [2]);
        FixedDialogService dialog = new(null,
        [
            OutgoingMailAttachment.FromLocalFile(firstPath),
            OutgoingMailAttachment.FromLocalFile(secondPath)
        ]);
        using MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()), dialog);
        OpenCompose(compose, Account(MailProviderType.Gmail));

        compose.AttachFilesCommand.Execute(null);
        Assert.Equal(2, compose.Draft!.Attachments.Count);
        Assert.All(compose.Draft.Attachments, item => Assert.Equal("same.txt", item.FileName));
        Assert.DoesNotContain(first.Path, compose.Draft.Attachments[0].FileName, StringComparison.OrdinalIgnoreCase);

        compose.RemoveAttachmentCommand.Execute(compose.Draft.Attachments[0]);
        Assert.Single(compose.Draft.Attachments);
    }

    [Theory]
    [InlineData("stage76-attachment-crash.txt", 3)]
    [InlineData("document.pdf", 2)]
    [InlineData("photo.jpg", 1)]
    [InlineData("empty.txt", 0)]
    [InlineData("отчёт № 1.txt", 4)]
    [InlineData("file with spaces.bin", 5)]
    public void NormalLocalFilesCanBeAddedWithoutWpfBindingFailure(string fileName, int size)
    {
        using TemporaryDirectory directory = new();
        string path = directory.CreateFile(fileName, Enumerable.Range(0, size).Select(value => (byte)value).ToArray());
        OutgoingMailAttachment attachment = OutgoingMailAttachment.FromLocalFile(path);
        using MailComposeViewModel compose = CreateCompose(
            new RecordingSendProvider(MailSendResult.Success()),
            new FixedDialogService(null, [attachment]));
        OpenCompose(compose, Account(MailProviderType.Gmail));
        int collectionAdds = 0;
        compose.Draft!.Attachments.CollectionChanged += (_, eventArgs) =>
            collectionAdds += eventArgs.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add ? 1 : 0;

        compose.AttachFilesCommand.Execute(null);

        MailComposeAttachmentItem item = Assert.Single(compose.Draft.Attachments);
        Assert.Equal(fileName, item.FileName);
        Assert.Equal(MailAttachmentSizeFormatter.Format(size), item.DisplaySize);
        Assert.Equal(1, collectionAdds);
    }

    [Fact]
    public void AddStoresMetadataReferenceWithoutOpeningOrReadingWholeFile()
    {
        using TemporaryDirectory directory = new();
        string path = directory.CreateFile("locked.bin", [1, 2, 3]);
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        OutgoingMailAttachment attachment = OutgoingMailAttachment.FromLocalFile(path);

        Assert.Equal(3, attachment.Size);
        Assert.IsType<LocalFileMailAttachmentSource>(attachment.Source);
        Assert.Equal(0, exclusive.Position);
    }

    [Fact]
    public void MissingOrInaccessibleSelectionBecomesControlledComposeError()
    {
        using TemporaryDirectory directory = new();
        Assert.Equal(
            MailAttachmentFailureKind.LocalReadFailure,
            Assert.Throws<MailAttachmentException>(() =>
                OutgoingMailAttachment.FromLocalFile(Path.Combine(directory.Path, "missing.txt"))).FailureKind);

        using MailComposeViewModel compose = CreateCompose(
            new RecordingSendProvider(MailSendResult.Success()),
            new ThrowingDialogService(new System.Security.SecurityException()));
        OpenCompose(compose, Account(MailProviderType.Gmail));

        compose.AttachFilesCommand.Execute(null);

        Assert.NotNull(compose.ErrorMessage);
        Assert.Empty(compose.Draft!.Attachments);
    }

    [Fact]
    public void AttachmentsRemainScopedToTheirComposeAccount()
    {
        using TemporaryDirectory directory = new();
        OutgoingMailAttachment firstAttachment = OutgoingMailAttachment.FromLocalFile(
            directory.CreateFile("first.txt", [1]));
        OutgoingMailAttachment secondAttachment = OutgoingMailAttachment.FromLocalFile(
            directory.CreateFile("second.txt", [2]));
        QueueDialogService dialog = new([firstAttachment], [secondAttachment]);
        using MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()), dialog);
        MailAccount first = Account(MailProviderType.Gmail);
        MailAccount second = Account(MailProviderType.Gmail);
        second.Id = Guid.NewGuid();

        OpenCompose(compose, first);
        compose.AttachFilesCommand.Execute(null);
        OpenCompose(compose, second);
        compose.AttachFilesCommand.Execute(null);
        compose.ActivateAccount(first);

        Assert.Equal("first.txt", Assert.Single(compose.Draft!.Attachments).FileName);
        compose.ActivateAccount(second);
        Assert.Equal("second.txt", Assert.Single(compose.Draft!.Attachments).FileName);
    }

    [Fact]
    public async Task MissingLocalFilePreventsSendAndPreservesComposeAttachment()
    {
        using TemporaryDirectory directory = new();
        string path = directory.CreateFile("missing.txt", [1, 2]);
        OutgoingMailAttachment attachment = OutgoingMailAttachment.FromLocalFile(path);
        File.Delete(path);
        RecordingAttachmentProvider unused = new([]);
        MailOutgoingAttachmentMaterializer materializer = new(
            new MailAttachmentContentProviderFactory([unused]));
        RecordingGmailApiClient api = new();
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            api,
            MimeFactory(),
            materializer);
        MailAccount account = Account(MailProviderType.Gmail);
        MailComposeRequest request = Request(account) with { Attachments = [attachment] };

        MailSendResult result = await provider.SendAsync(account, request);

        Assert.Equal(MailSendFailureKind.AttachmentUnavailable, result.FailureKind);
        Assert.Equal(0, api.SendCount);
    }

    [Fact]
    public async Task ChangedLocalFilePreventsSendBeforeNetwork()
    {
        using TemporaryDirectory directory = new();
        string path = directory.CreateFile("changed.txt", [1]);
        OutgoingMailAttachment attachment = OutgoingMailAttachment.FromLocalFile(path);
        await File.WriteAllBytesAsync(path, [2]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        RecordingGmailApiClient api = new();
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            api,
            MimeFactory(),
            new MailOutgoingAttachmentMaterializer(
                new MailAttachmentContentProviderFactory([new RecordingAttachmentProvider([])])));
        MailAccount account = Account(MailProviderType.Gmail);

        MailSendResult result = await provider.SendAsync(
            account,
            Request(account) with { Attachments = [attachment] });

        Assert.Equal(MailSendFailureKind.AttachmentUnavailable, result.FailureKind);
        Assert.Equal(0, api.SendCount);
    }

    [Fact]
    public async Task FailedForwardSourceRetrievalPreventsIncompleteSend()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        OutgoingMailAttachment source = OutgoingMailAttachment.FromSource(
            account.Id,
            "gmail:message",
            Info("mime:0.1", "source.pdf", 10));
        RecordingGmailApiClient api = new();
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            api,
            MimeFactory(),
            new MailOutgoingAttachmentMaterializer(
                new MailAttachmentContentProviderFactory([new ThrowingAttachmentProvider()])));

        MailSendResult result = await provider.SendAsync(
            account,
            Request(account) with { Attachments = [source] });

        Assert.Equal(MailSendFailureKind.AttachmentUnavailable, result.FailureKind);
        Assert.Equal(0, api.SendCount);
    }

    [Fact]
    public async Task FailedSendPreservesAttachmentsSuccessfulSendClearsThem()
    {
        using TemporaryDirectory directory = new();
        OutgoingMailAttachment attachment = OutgoingMailAttachment.FromLocalFile(
            directory.CreateFile("draft.txt", [1]));
        FixedDialogService dialog = new(null, [attachment]);
        RecordingSendProvider failed = new(MailSendResult.Failure(
            MailSendFailureKind.MessageRejected,
            "Rejected"));
        using MailComposeViewModel compose = CreateCompose(failed, dialog);
        MailAccount account = Account(MailProviderType.Gmail);
        OpenCompose(compose, account);
        compose.AttachFilesCommand.Execute(null);

        await compose.SendCommand.ExecuteAsync(null);
        Assert.Single(compose.Draft!.Attachments);

        failed.Result = MailSendResult.Success();
        await compose.SendCommand.ExecuteAsync(null);
        Assert.Null(compose.Draft);
        Assert.Equal(0, compose.DraftCount);
    }

    [Fact]
    public async Task DiscardAndDisposeClearRamOnlyAttachmentReferences()
    {
        using TemporaryDirectory directory = new();
        FixedDialogService dialog = new(null,
            [OutgoingMailAttachment.FromLocalFile(directory.CreateFile("draft.txt", [1]))]);
        MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()), dialog);
        OpenCompose(compose, Account(MailProviderType.Gmail));
        compose.AttachFilesCommand.Execute(null);

        await compose.CancelCommand.ExecuteAsync(null);
        Assert.Equal(0, compose.DraftCount);

        OpenCompose(compose, Account(MailProviderType.Gmail));
        compose.AttachFilesCommand.Execute(null);
        compose.Dispose();
        Assert.Equal(0, compose.DraftCount);
    }

    [Fact]
    public async Task AttachmentOnlyMessageDoesNotTriggerEmptyMessageConfirmation()
    {
        using TemporaryDirectory directory = new();
        RecordingConfirmationService confirmation = new() { EmptyResult = false };
        RecordingSendProvider send = new(MailSendResult.Success());
        FixedDialogService dialog = new(null,
            [OutgoingMailAttachment.FromLocalFile(directory.CreateFile("only.bin", [1]))]);
        using MailComposeViewModel compose = new(
            new FixedSendProviderFactory(send),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            confirmation,
            dialog);
        compose.ActivateAccount(Account(MailProviderType.Gmail));
        compose.NewMessageCommand.Execute(null);
        compose.Draft!.To = "to@example.test";
        compose.AttachFilesCommand.Execute(null);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Equal(0, confirmation.EmptyCount);
        Assert.Null(compose.Draft);
    }

    [Fact]
    public async Task ReplyCanIncludeNewLocalAttachmentWithoutCopyingSourceAttachment()
    {
        using TemporaryDirectory directory = new();
        OutgoingMailAttachment local = OutgoingMailAttachment.FromLocalFile(
            directory.CreateFile("new.txt", [1]));
        RecordingSendProvider send = new(MailSendResult.Success());
        using MailComposeViewModel compose = CreateCompose(send, new FixedDialogService(null, [local]));
        compose.ActivateAccount(Account(MailProviderType.Gmail));
        MailMessageContent source = ContentWithAttachments(
            [Info("mime:0.1", "original.pdf", 4)],
            new MailReplyMetadata("reply@example.test", "<source@example.test>", []));
        await compose.ReplyCommand.ExecuteAsync(source);
        compose.AttachFilesCommand.Execute(null);

        await compose.SendCommand.ExecuteAsync(null);

        OutgoingMailAttachment sent = Assert.Single(send.LastRequest!.Attachments);
        Assert.Equal("new.txt", sent.FileName);
        Assert.NotNull(send.LastRequest.ReplyContext);
    }

    [Fact]
    public void MimeWithoutAttachmentsRemainsTextPlain()
    {
        MimeMessage message = MimeFactory().Create(Account(MailProviderType.Gmail), Request(Account(MailProviderType.Gmail))).Message;
        Assert.IsType<TextPart>(message.Body);
        Assert.Equal("text/plain", message.Body.ContentType.MimeType);
    }

    [Fact]
    public void MimeWithAttachmentsIsMultipartMixedAndPreservesExactBytes()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        byte[] first = [0, 1, 2];
        byte[] second = Encoding.UTF8.GetBytes("unicode");
        MimeMessage message = MimeFactory().Create(
            account,
            Request(account),
            [
                new MaterializedMailAttachment("first.bin", "application/octet-stream", first),
                new MaterializedMailAttachment("второй.txt", "text/plain", second)
            ]).Message;

        Multipart mixed = Assert.IsType<Multipart>(message.Body);
        Assert.Equal("mixed", mixed.ContentType.MediaSubtype);
        TextPart text = Assert.IsType<TextPart>(mixed[0]);
        Assert.Equal("utf-8", text.ContentType.Charset, ignoreCase: true);
        MimePart[] attachments = message.Attachments.Cast<MimePart>().ToArray();
        Assert.Equal(2, attachments.Length);
        Assert.Equal(first, Decode(attachments[0]));
        Assert.Equal(second, Decode(attachments[1]));
        Assert.All(attachments, part => Assert.True(part.ContentDisposition?.IsAttachment));
    }

    [Fact]
    public void MimeSupportsZeroByteAttachmentAndUnknownTypeFallsBackSafely()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        MimeMessage message = MimeFactory().Create(
            account,
            Request(account),
            [new MaterializedMailAttachment("data.unknown-extension", "not a mime", ReadOnlyMemory<byte>.Empty)])
            .Message;

        MimePart attachment = (MimePart)Assert.Single(message.Attachments);
        Assert.Equal("application/octet-stream", attachment.ContentType.MimeType);
        Assert.Empty(Decode(attachment));
    }

    [Fact]
    public async Task GmailAttachmentUsesMessagesSendRawMimeAndNoImap()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        RecordingGmailApiClient api = new();
        FixedMaterializer materializer = new(
            [new MaterializedMailAttachment("file.txt", "text/plain", Encoding.UTF8.GetBytes("file"))]);
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()), api, MimeFactory(), materializer);
        MailComposeRequest request = Request(account) with
        {
            Attachments = [PlaceholderOutgoing("file.txt", 4)]
        };

        MailSendResult result = await provider.SendAsync(account, request);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, api.SendCount);
        using MemoryStream stream = new(api.RawMime!, writable: false);
        MimeMessage sent = MimeMessage.Load(stream);
        Assert.Single(sent.Attachments);
        Assert.Equal("file.txt", ((MimePart)sent.Attachments.Single()).FileName);
    }

    [Fact]
    public async Task GmailReplyWithAttachmentKeepsThreadAndForwardHasNone()
    {
        MailAccount account = Account(MailProviderType.Gmail);
        RecordingGmailApiClient api = new();
        GmailMailSendProvider provider = new(
            new FixedCredentialStore(ModifyCredential()),
            api,
            MimeFactory(),
            new FixedMaterializer([new MaterializedMailAttachment("file.bin", "application/octet-stream", new byte[] { 1 })]));
        MailComposeRequest reply = Request(account) with
        {
            ReplyContext = new MailReplyContext("<source@example.test>", ["<source@example.test>"])
            {
                ProviderThreadId = "thread-id"
            },
            Attachments = [PlaceholderOutgoing("file.bin", 1)]
        };

        await provider.SendAsync(account, reply);
        Assert.Equal("thread-id", api.ThreadId);

        api.Reset();
        MailComposeRequest forward = Request(account) with { Attachments = [PlaceholderOutgoing("file.bin", 1)] };
        await provider.SendAsync(account, forward);
        Assert.Null(api.ThreadId);
    }

    [Fact]
    public void GmailKnownRawMessageLimitIsCentralizedAndRejectsBeforeApiCall()
    {
        Assert.Equal(35L * 1024 * 1024, MailAttachmentLimits.GmailMaximumRawMessageBytes);
        Assert.Throws<MailAttachmentException>(() =>
            MailAttachmentLimits.ValidateGmailRawMessageSize(MailAttachmentLimits.GmailMaximumRawMessageBytes + 1));
    }

    [Theory]
    [InlineData(MailProviderType.Yandex)]
    [InlineData(MailProviderType.MailRu)]
    [InlineData(MailProviderType.GenericImap)]
    public async Task SmtpAttachmentUsesSameMimeMessageForSendAndSentCopy(MailProviderType providerType)
    {
        MailAccount account = Account(providerType);
        RecordingSmtpClient smtp = new();
        RecordingSentCopyClient sent = new();
        SmtpMailSendProvider provider = SmtpProvider(
            smtp,
            sent,
            new FixedMaterializer([new MaterializedMailAttachment("file.bin", "application/octet-stream", new byte[] { 7, 8 })]));
        MailComposeRequest request = Request(account) with { Attachments = [PlaceholderOutgoing("file.bin", 2)] };

        MailSendResult result = await provider.SendAsync(account, request);

        Assert.True(result.IsSuccess);
        Assert.Same(smtp.Message, sent.Message);
        Assert.Equal(smtp.Message!.MessageId, sent.Message!.MessageId);
        Assert.Equal(smtp.Message.Date, sent.Message.Date);
        Assert.Equal([7, 8], Decode((MimePart)Assert.Single(sent.Message.Attachments)));
        Assert.Equal(MessageFlags.Seen, sent.Flags);
    }

    [Fact]
    public async Task SmtpAttachmentFailurePerformsZeroSentAppendAndNoRetry()
    {
        MailAccount account = Account(MailProviderType.Yandex);
        RecordingSmtpClient smtp = new() { Failure = new MailSubmissionException(MailSendFailureKind.MessageRejected, "Rejected") };
        RecordingSentCopyClient sent = new();
        SmtpMailSendProvider provider = SmtpProvider(
            smtp,
            sent,
            new FixedMaterializer([new MaterializedMailAttachment("file.bin", "application/octet-stream", new byte[] { 1 })]));

        MailSendResult result = await provider.SendAsync(
            account,
            Request(account) with { Attachments = [PlaceholderOutgoing("file.bin", 1)] });

        Assert.False(result.IsMessageSent);
        Assert.Equal(1, smtp.SendCount);
        Assert.Equal(0, sent.AppendCount);
    }

    [Fact]
    public async Task SentAppendFailureKeepsPartialSuccessSemanticsWithAttachments()
    {
        MailAccount account = Account(MailProviderType.Yandex);
        RecordingSentCopyClient sent = new()
        {
            Result = ImapSentCopyResult.Failed(MailSentCopyFailureKind.AppendRejected)
        };
        SmtpMailSendProvider provider = SmtpProvider(
            new RecordingSmtpClient(),
            sent,
            new FixedMaterializer([new MaterializedMailAttachment("file.bin", "application/octet-stream", new byte[] { 1 })]));

        MailSendResult result = await provider.SendAsync(
            account,
            Request(account) with { Attachments = [PlaceholderOutgoing("file.bin", 1)] });

        Assert.Equal(MailSendOutcome.SentButCopyNotSaved, result.Outcome);
        Assert.Equal(1, sent.AppendCount);
    }

    [Fact]
    public void ReplyDoesNotOfferOriginalAttachmentsAndKeepsThreading()
    {
        MailMessageContent source = ContentWithAttachments(
            [Info("mime:0.1", "source.pdf", 10)],
            new MailReplyMetadata("reply@example.test", "<id@example.test>", ["<old@example.test>"])
            {
                ProviderThreadId = "thread"
            });

        MailComposeTemplate reply = new MailComposePreparationService().CreateReply(source);
        Assert.Empty(reply.ForwardAttachments);
        Assert.Equal("thread", reply.ReplyContext!.ProviderThreadId);
        Assert.Equal("<id@example.test>", reply.ReplyContext.InReplyTo);
    }

    [Fact]
    public async Task ForwardOffersOnlyRealAttachmentsDefaultUnselectedAndNoThreadContext()
    {
        MailMessageContent source = ContentWithAttachments(
        [
            Info("mime:0.1", "report.pdf", 10),
            new MailAttachmentInfo("mime:0.2", "logo.png", "image/png", 2, true, true)
        ]);
        MailComposeTemplate template = new MailComposePreparationService().CreateForward(source);
        MailAccount account = Account(MailProviderType.Gmail);
        using MailComposeViewModel compose = CreateCompose(new RecordingSendProvider(MailSendResult.Success()));
        compose.ActivateAccount(account);

        await compose.ForwardCommand.ExecuteAsync(source);

        MailComposeAttachmentItem item = Assert.Single(compose.Draft!.Attachments);
        Assert.Equal("report.pdf", item.FileName);
        Assert.True(item.IsForwardedSource);
        Assert.False(item.IsSelected);
        Assert.False(item.IsIncluded);
        Assert.Null(compose.Draft.ReplyContext);
    }

    [Fact]
    public async Task ForwardSelectionIncludesOnlyChosenSourceAlongsideLocalFile()
    {
        using TemporaryDirectory directory = new();
        OutgoingMailAttachment local = OutgoingMailAttachment.FromLocalFile(
            directory.CreateFile("local.txt", [3]));
        RecordingSendProvider send = new(MailSendResult.Success());
        using MailComposeViewModel compose = CreateCompose(send, new FixedDialogService(null, [local]));
        MailAccount account = Account(MailProviderType.Gmail);
        MailMessageContent source = ContentWithAttachments(
        [
            Info("mime:0.1", "one.pdf", 1),
            Info("mime:0.2", "two.pdf", 2)
        ]);
        compose.ActivateAccount(account);
        await compose.ForwardCommand.ExecuteAsync(source);
        compose.Draft!.To = "to@example.test";
        compose.Draft.Attachments[1].IsSelected = true;
        compose.AttachFilesCommand.Execute(null);

        await compose.SendCommand.ExecuteAsync(null);

        Assert.NotNull(send.LastRequest);
        Assert.Equal(2, send.LastRequest!.Attachments.Count);
        Assert.Contains(send.LastRequest.Attachments, item => item.FileName == "two.pdf");
        Assert.Contains(send.LastRequest.Attachments, item => item.FileName == "local.txt");
        Assert.DoesNotContain(send.LastRequest.Attachments, item => item.FileName == "one.pdf");
        Assert.Null(send.LastRequest.ReplyContext);
    }

    [Fact]
    public async Task ForwardCanSelectSeveralOriginalAttachments()
    {
        RecordingSendProvider send = new(MailSendResult.Success());
        using MailComposeViewModel compose = CreateCompose(send);
        compose.ActivateAccount(Account(MailProviderType.Gmail));
        MailMessageContent source = ContentWithAttachments(
        [
            Info("mime:0.1", "one.bin", 1),
            Info("mime:0.2", "two.bin", 2),
            Info("mime:0.3", "three.bin", 3)
        ]);
        await compose.ForwardCommand.ExecuteAsync(source);
        compose.Draft!.To = "to@example.test";
        compose.Draft.Attachments[0].IsSelected = true;
        compose.Draft.Attachments[2].IsSelected = true;

        await compose.SendCommand.ExecuteAsync(null);

        Assert.Equal(["one.bin", "three.bin"], send.LastRequest!.Attachments.Select(item => item.FileName));
    }

    [Fact]
    public void ProviderLimitsDoNotInventGenericSmtpMaximum()
    {
        OutgoingMailAttachment largeMetadata = PlaceholderOutgoing(
            "large.bin",
            MailAttachmentLimits.MailRuMaximumSingleAttachmentBytes + 1);
        MailAttachmentLimits.ValidateMetadata(MailProviderType.GenericImap, [largeMetadata]);
        Assert.Throws<MailAttachmentException>(() =>
            MailAttachmentLimits.ValidateMetadata(MailProviderType.MailRu, [largeMetadata]));
    }

    [Fact]
    public void YandexConfirmedAggregateLimitIsValidatedBeforeMaterialization()
    {
        OutgoingMailAttachment first = PlaceholderOutgoing("first.bin", 13L * 1024 * 1024);
        OutgoingMailAttachment second = PlaceholderOutgoing("second.bin", 13L * 1024 * 1024);

        Assert.Throws<MailAttachmentException>(() =>
            MailAttachmentLimits.ValidateMetadata(MailProviderType.Yandex, [first, second]));
    }

    [Fact]
    public void Stage76SecurityAndPersistenceGuardsRemainExplicit()
    {
        string app = FindRepositoryFile("src", "UnifiedMessenger.App", "Services", "Mail", "MailAttachmentCore.cs");
        string source = File.ReadAllText(app);
        string settings = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "Models", "AppSettings.cs"));
        string project = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "UnifiedMessenger.App.csproj"));

        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtocolLogger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Attachment", settings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CurrentSchemaVersion = 4", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference Include=\"MimeTypes", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachmentUiUsesNativeDialogsAndProviderNeutralCommands()
    {
        string xaml = File.ReadAllText(FindRepositoryFile("src", "UnifiedMessenger.App", "Views", "MailInboxView.xaml"));
        string dialog = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Mail", "WpfMailAttachmentDialogService.cs"));

        Assert.Contains("SelectedMessageContent.Attachments", xaml, StringComparison.Ordinal);
        Assert.Contains("SaveAttachmentCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("AttachFilesCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("IsForwardedSource", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding FileName, Mode=OneWay}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding DisplaySize, Mode=OneWay}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Run Text=\"{Binding FileName}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Run Text=\"{Binding DisplaySize}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Run Text=\"{Binding ContentType}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Multiselect = true", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView2", dialog, StringComparison.Ordinal);
    }

    private static MailMessageContent Extract(MimeMessage message) =>
        new MailContentExtractor(new MailHtmlSanitizer()).Extract("message-key", message, true);

    private static MimeMessage BasicMessage()
    {
        MimeMessage message = new() { Subject = "Subject", Date = FixedNow };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(MailboxAddress.Parse("recipient@example.test"));
        return message;
    }

    private static MimeMessage MessageWithAttachments(params (string Name, string Type, byte[] Bytes)[] values)
    {
        MimeMessage message = BasicMessage();
        BodyBuilder builder = new() { TextBody = "Body" };
        foreach ((string name, string type, byte[] bytes) in values)
        {
            builder.Attachments.Add(name, bytes, ContentType.Parse(type));
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private static MailAttachmentInfo Info(string key, string name, long size) =>
        new(key, name, MailAttachmentContentType.Resolve(name), size, false, true);

    private static MailMessageContent ContentWithAttachments(
        IReadOnlyList<MailAttachmentInfo> attachments,
        MailReplyMetadata? replyMetadata = null) =>
        new MailMessageContent(
            "message-key",
            "Subject",
            "Sender",
            "sender@example.test",
            "recipient@example.test",
            FixedNow,
            MailMessageBodyKind.PlainText,
            "Body",
            [],
            true,
            attachments.Count > 0,
            "Body",
            replyMetadata)
        {
            Attachments = attachments
        };

    private static MailAccount Account(MailProviderType provider) => new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Provider = provider,
        EmailAddress = "sender@example.test",
        DisplayName = "Sender",
        CredentialKey = "test-credential-key",
        AuthenticationKind = provider is MailProviderType.Gmail
            ? MailAuthenticationKind.OAuth
            : MailAuthenticationKind.Password,
        IsEnabled = true,
        GenericConnectionSettings = provider is MailProviderType.GenericImap
            ? new MailConnectionSettings
            {
                Imap = new MailServerSettings
                {
                    Host = "imap.example.test",
                    Port = 993,
                    Username = "sender@example.test",
                    SecureSocketMode = MailSecureSocketMode.SslOnConnect
                },
                Smtp = new MailServerSettings
                {
                    Host = "smtp.example.test",
                    Port = 465,
                    Username = "sender@example.test",
                    SecureSocketMode = MailSecureSocketMode.SslOnConnect
                }
            }
            : null
    };

    private static MailComposeRequest Request(MailAccount account) =>
        new MailComposeRequestFactory().Create(
            account,
            new MailComposeInput("to@example.test", string.Empty, string.Empty, "Subject", "Body"));

    private static MailMimeMessageFactory MimeFactory() => new(new FixedTimeProvider(FixedNow));

    private static OutgoingMailAttachment PlaceholderOutgoing(string fileName, long size) =>
        new(Guid.NewGuid().ToString("N"), fileName, MailAttachmentContentType.Resolve(fileName), size);

    private static byte[] SerializeBytes(MimeMessage message)
    {
        using MemoryStream stream = new();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private static byte[] Decode(MimePart part)
    {
        using MemoryStream stream = new();
        part.Content!.DecodeTo(stream);
        return stream.ToArray();
    }

    private static MailCredential ModifyCredential() =>
        MailCredential.CreateGmailOAuth(
            "fake-refresh-token",
            "fake-client-id",
            "fake-client-secret",
            GmailOAuthConstants.ModifyScope);

    private static MailComposeViewModel CreateCompose(
        IMailSendProvider provider,
        IMailAttachmentDialogService? dialog = null) =>
        new(
            new FixedSendProviderFactory(provider),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            new AlwaysConfirmService(),
            dialog);

    private static void OpenCompose(MailComposeViewModel compose, MailAccount account)
    {
        compose.ActivateAccount(account);
        compose.NewMessageCommand.Execute(null);
        compose.Draft!.To = "to@example.test";
        compose.Draft.Subject = "Subject";
        compose.Draft.TextBody = "Body";
    }

    private static SmtpMailSendProvider SmtpProvider(
        ISmtpSubmissionClient smtp,
        IImapSentCopyClient sent,
        IMailOutgoingAttachmentMaterializer materializer)
    {
        NoOpConnectionValidator validator = new();
        MailProviderFactory providers = new(
        [
            new GmailApiProvider(),
            new YandexMailProvider(validator),
            new MailRuMailProvider(validator),
            new GenericImapMailProvider(validator)
        ]);
        return new SmtpMailSendProvider(
            new FixedCredentialStore(MailCredential.CreatePassword("fake-app-password")),
            providers,
            smtp,
            sent,
            MimeFactory(),
            materializer);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Repository file was not found.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "UnifiedMessenger.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string ParentPath => Directory.GetParent(Path)!.FullName;
        public string Name => new DirectoryInfo(Path).Name;

        public string CreateFile(string name, byte[] bytes)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FixedDialogService(
        string? destination,
        IReadOnlyList<OutgoingMailAttachment>? selected = null) : IMailAttachmentDialogService
    {
        public IReadOnlyList<OutgoingMailAttachment> SelectOutgoingAttachments() => selected ?? [];
        public string? SelectSaveDestination(MailAttachmentInfo attachment) => destination;
    }

    private sealed class ThrowingDialogService(Exception exception) : IMailAttachmentDialogService
    {
        public IReadOnlyList<OutgoingMailAttachment> SelectOutgoingAttachments() => throw exception;
        public string? SelectSaveDestination(MailAttachmentInfo attachment) => null;
    }

    private sealed class QueueDialogService(
        params IReadOnlyList<OutgoingMailAttachment>[] selections) : IMailAttachmentDialogService
    {
        private readonly Queue<IReadOnlyList<OutgoingMailAttachment>> _selections = new(selections);

        public IReadOnlyList<OutgoingMailAttachment> SelectOutgoingAttachments() => _selections.Dequeue();
        public string? SelectSaveDestination(MailAttachmentInfo attachment) => null;
    }

    private sealed class RecordingAttachmentProvider(byte[] bytes) : IMailAttachmentContentProvider
    {
        public int GetCount { get; private set; }
        public bool Supports(MailProviderType providerType) => true;

        public Task<MailAttachmentContent> GetAsync(
            MailAccount account,
            string messageKey,
            string attachmentKey,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(new MailAttachmentContent("attachment.bin", "application/octet-stream", bytes));
        }
    }

    private sealed class ThrowingAttachmentProvider : IMailAttachmentContentProvider
    {
        public bool Supports(MailProviderType providerType) => true;
        public Task<MailAttachmentContent> GetAsync(MailAccount account, string messageKey, string attachmentKey, CancellationToken cancellationToken = default) =>
            Task.FromException<MailAttachmentContent>(new MailAttachmentException(
                MailAttachmentFailureKind.ProviderFailure,
                "Не удалось загрузить вложение."));
    }

    private sealed class FixedCredentialStore(MailCredential? credential) : IMailCredentialStore
    {
        public Task SaveAsync(string credentialKey, MailCredential value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MailCredential?> LoadAsync(string credentialKey, CancellationToken cancellationToken = default) => Task.FromResult(credential);
        public Task DeleteAsync(string credentialKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingGmailReadClient(byte[] raw) : IGmailApiReadClient
    {
        public int RawMessageCount { get; private set; }
        public Task<GmailApiInboxPage> GetInboxPageAsync(MailCredential credential, Guid accountId, string? pageToken, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GmailApiInboxPage([], null));

        public Task<GmailApiRawMessage> GetRawMessageAsync(MailCredential credential, Guid accountId, string messageId, CancellationToken cancellationToken = default)
        {
            RawMessageCount++;
            return Task.FromResult(new GmailApiRawMessage(raw, true, "thread"));
        }
    }

    private sealed class FixedMaterializer(IReadOnlyList<MaterializedMailAttachment> attachments)
        : IMailOutgoingAttachmentMaterializer
    {
        public Task<IReadOnlyList<MaterializedMailAttachment>> MaterializeAsync(
            MailAccount account,
            IReadOnlyList<OutgoingMailAttachment> requested,
            CancellationToken cancellationToken = default) => Task.FromResult(attachments);
    }

    private sealed class RecordingGmailApiClient : IGmailApiSendClient
    {
        public int SendCount { get; private set; }
        public byte[]? RawMime { get; private set; }
        public string? ThreadId { get; private set; }

        public Task<GmailApiSendReceipt> SendAsync(MailCredential credential, Guid accountId, byte[] rawMime, string? threadId, CancellationToken cancellationToken = default)
        {
            SendCount++;
            RawMime = rawMime;
            ThreadId = threadId;
            return Task.FromResult(new GmailApiSendReceipt("sent", threadId));
        }

        public void Reset()
        {
            RawMime = null;
            ThreadId = null;
        }
    }

    private sealed class RecordingSmtpClient : ISmtpSubmissionClient
    {
        public int SendCount { get; private set; }
        public MimeMessage? Message { get; private set; }
        public MailSubmissionException? Failure { get; init; }

        public Task SendAsync(MailServerSettings server, string secret, MimeMessage message, MailboxAddress envelopeSender, IReadOnlyList<MailboxAddress> envelopeRecipients, CancellationToken cancellationToken = default)
        {
            SendCount++;
            Message = message;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class RecordingSentCopyClient : IImapSentCopyClient
    {
        public int AppendCount { get; private set; }
        public MimeMessage? Message { get; private set; }
        public MessageFlags Flags { get; private set; }
        public ImapSentCopyResult Result { get; init; } = ImapSentCopyResult.Saved;

        public Task<ImapSentCopyResult> AppendAsync(MailServerSettings server, string secret, MimeMessage message, MessageFlags flags, CancellationToken cancellationToken = default)
        {
            AppendCount++;
            Message = message;
            Flags = flags;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedSendProviderFactory(IMailSendProvider provider) : IMailSendProviderFactory
    {
        public IMailSendProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class RecordingSendProvider(MailSendResult result) : IMailSendProvider
    {
        public MailSendResult Result { get; set; } = result;
        public MailComposeRequest? LastRequest { get; private set; }
        public bool Supports(MailProviderType providerType) => true;
        public Task<MailSendResult> SendAsync(MailAccount account, MailComposeRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class AlwaysConfirmService : IMailComposeConfirmationService
    {
        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingConfirmationService : IMailComposeConfirmationService
    {
        public bool EmptyResult { get; init; } = true;
        public int EmptyCount { get; private set; }
        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default)
        {
            EmptyCount++;
            return Task.FromResult(EmptyResult);
        }

        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FixedReadProviderFactory(IMailReadProvider provider) : IMailReadProviderFactory
    {
        public IMailReadProvider Get(MailProviderType providerType) => provider;
    }

    private sealed class SingleMessageReadProvider(MailMessageContent content) : IMailReadProvider
    {
        public bool Supports(MailProviderType providerType) => true;
        public Task<MailPage<MailMessageSummary>> GetInboxPageAsync(MailAccount account, string? continuationToken, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailPage<MailMessageSummary>(
                [new MailMessageSummary(content.MessageKey, content.Subject, content.FromDisplayName, content.FromAddress, content.ReceivedAt, string.Empty, content.IsUnread)],
                null));

        public Task<MailMessageContent> GetMessageAsync(MailAccount account, string messageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(content);
    }

    private sealed class DeferredSaveService : IMailAttachmentSaveService
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MailAttachmentSaveResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCanceled { get; private set; }

        public async Task<MailAttachmentSaveResult> SaveAsync(MailAccount account, string messageKey, MailAttachmentInfo attachment, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            try
            {
                return await Completion.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
                throw;
            }
        }
    }

    private sealed class NoOpConnectionValidator : IMailConnectionValidator
    {
        public Task<MailConnectionValidationResult> ValidateAsync(MailConnectionSettings settings, string emailAddress, string secret, CancellationToken cancellationToken = default) =>
            Task.FromResult(MailConnectionValidationResult.Success(new MailIdentity(emailAddress, null)));
    }
}
