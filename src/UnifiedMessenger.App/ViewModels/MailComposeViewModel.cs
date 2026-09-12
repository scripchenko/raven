using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.Mail;

namespace UnifiedMessenger.App.ViewModels;

public sealed class MailComposeDraft : ObservableObject
{
    private string _to = string.Empty;
    private string _cc = string.Empty;
    private string _bcc = string.Empty;
    private string _subject = string.Empty;
    private string _textBody = string.Empty;
    private bool _areCopyFieldsVisible;

    internal MailComposeDraft(MailComposeTemplate template, Guid accountId)
    {
        _to = template.To;
        _cc = template.Cc;
        _bcc = template.Bcc;
        _subject = template.Subject;
        _textBody = template.TextBody;
        _areCopyFieldsVisible = !string.IsNullOrWhiteSpace(_cc) || !string.IsNullOrWhiteSpace(_bcc);
        ReplyContext = template.ReplyContext;
        foreach (MailForwardAttachmentOffer offer in template.ForwardAttachments)
        {
            AddAttachmentItem(new MailComposeAttachmentItem(
                OutgoingMailAttachment.FromSource(accountId, offer.MessageKey, offer.Attachment),
                isForwardedSource: true,
                isSelected: false));
        }

        foreach (OutgoingMailAttachment attachment in template.ExistingAttachments)
        {
            AddAttachmentItem(new MailComposeAttachmentItem(
                attachment,
                isForwardedSource: false,
                isSelected: true));
        }

        IsReadOnly = template.IsReadOnly;
        RestrictionMessage = template.RestrictionMessage;
    }

    internal event EventHandler? Changed;

    public string To
    {
        get => _to;
        set => SetDraftProperty(ref _to, value ?? string.Empty);
    }

    public string Cc
    {
        get => _cc;
        set => SetDraftProperty(ref _cc, value ?? string.Empty);
    }

    public string Bcc
    {
        get => _bcc;
        set => SetDraftProperty(ref _bcc, value ?? string.Empty);
    }

    public string Subject
    {
        get => _subject;
        set => SetDraftProperty(ref _subject, value ?? string.Empty);
    }

    public string TextBody
    {
        get => _textBody;
        set => SetDraftProperty(ref _textBody, value ?? string.Empty);
    }

    public bool AreCopyFieldsVisible
    {
        get => _areCopyFieldsVisible;
        set => SetProperty(ref _areCopyFieldsVisible, value);
    }

    internal MailReplyContext? ReplyContext { get; }
    internal bool IsReadOnly { get; }
    internal string? RestrictionMessage { get; }

    public ObservableCollection<MailComposeAttachmentItem> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasUserContent =>
        !string.IsNullOrWhiteSpace(To)
        || !string.IsNullOrWhiteSpace(Cc)
        || !string.IsNullOrWhiteSpace(Bcc)
        || !string.IsNullOrWhiteSpace(Subject)
        || !string.IsNullOrWhiteSpace(TextBody)
        || Attachments.Any(item => item.IsIncluded);

    internal MailComposeInput Snapshot() =>
        new MailComposeInput(To, Cc, Bcc, Subject, TextBody, ReplyContext)
        {
            Attachments = Attachments
                .Where(item => item.IsIncluded)
                .Select(item => item.Attachment)
                .ToArray()
        };

    internal void AddLocalAttachments(IEnumerable<OutgoingMailAttachment> attachments)
    {
        foreach (OutgoingMailAttachment attachment in attachments)
        {
            AddAttachmentItem(new MailComposeAttachmentItem(
                attachment,
                isForwardedSource: false,
                isSelected: true));
        }

        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasUserContent));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RemoveAttachment(MailComposeAttachmentItem item)
    {
        item.PropertyChanged -= OnAttachmentPropertyChanged;
        Attachments.Remove(item);
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasUserContent));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AddAttachmentItem(MailComposeAttachmentItem item)
    {
        item.PropertyChanged += OnAttachmentPropertyChanged;
        Attachments.Add(item);
    }

    private void OnAttachmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MailComposeAttachmentItem.IsSelected))
        {
            OnPropertyChanged(nameof(HasUserContent));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetDraftProperty(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged(nameof(HasUserContent));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class MailComposeAttachmentItem : ObservableObject
{
    private bool _isSelected;

    internal MailComposeAttachmentItem(
        OutgoingMailAttachment attachment,
        bool isForwardedSource,
        bool isSelected)
    {
        Attachment = attachment;
        IsForwardedSource = isForwardedSource;
        _isSelected = isSelected;
    }

    internal OutgoingMailAttachment Attachment { get; }
    public string FileName => Attachment.FileName;
    public string DisplaySize => MailAttachmentSizeFormatter.Format(Attachment.Size);
    public bool IsForwardedSource { get; }
    public bool IsLocalFile => !IsForwardedSource;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(IsIncluded));
            }
        }
    }

    public bool IsIncluded => !IsForwardedSource || IsSelected;
}

public sealed class MailSentEventArgs(Guid accountId, bool sentCopySaved) : EventArgs
{
    public Guid AccountId { get; } = accountId;
    public bool SentCopySaved { get; } = sentCopySaved;
}

public enum GmailDraftChangeKind
{
    Created,
    Updated,
    Deleted,
    Sent
}

public sealed class GmailDraftChangedEventArgs(Guid accountId, GmailDraftChangeKind kind) : EventArgs
{
    public Guid AccountId { get; } = accountId;
    public GmailDraftChangeKind Kind { get; } = kind;
}

public sealed class MailComposeViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan GmailDraftAutosaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FinalAutosaveTimeout = TimeSpan.FromSeconds(5);
    private readonly IMailSendProviderFactory _providerFactory;
    private readonly IMailComposeRequestFactory _requestFactory;
    private readonly IMailComposePreparationService _preparationService;
    private readonly IMailComposeConfirmationService _confirmationService;
    private readonly IMailAttachmentDialogService? _attachmentDialogService;
    private readonly IGmailDraftService? _gmailDraftService;
    private readonly IMailDraftAutosaveScheduler _draftAutosaveScheduler;
    private readonly Dictionary<Guid, MailComposeDraft> _drafts = [];
    private readonly Dictionary<Guid, GmailComposeDraftState> _gmailDraftStates = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private MailAccount? _activeAccount;
    private MailComposeDraft? _draft;
    private bool _isSending;
    private string? _errorMessage;
    private string? _statusMessage;
    private string? _draftSaveStatusText;
    private MailSendFailureKind? _failureKind;
    private int _sendGate;
    private bool _disposed;

    public MailComposeViewModel(
        IMailSendProviderFactory providerFactory,
        IMailComposeRequestFactory requestFactory,
        IMailComposePreparationService preparationService,
        IMailComposeConfirmationService confirmationService,
        IMailAttachmentDialogService? attachmentDialogService = null,
        IGmailDraftService? gmailDraftService = null,
        IMailDraftAutosaveScheduler? draftAutosaveScheduler = null)
    {
        _providerFactory = providerFactory;
        _requestFactory = requestFactory;
        _preparationService = preparationService;
        _confirmationService = confirmationService;
        _attachmentDialogService = attachmentDialogService;
        _gmailDraftService = gmailDraftService;
        _draftAutosaveScheduler = draftAutosaveScheduler ?? new SystemMailDraftAutosaveScheduler();
        NewMessageCommand = new RelayCommand(StartNewMessage, CanStartNewMessage);
        RevealCopyFieldsCommand = new RelayCommand(RevealCopyFields, CanRevealCopyFields);
        ReplyCommand = new AsyncRelayCommand<MailMessageContent>(StartReplyAsync, CanPrepareFromMessage);
        ReplyAllCommand = new AsyncRelayCommand<MailMessageContent>(StartReplyAllAsync, CanPrepareReplyAllFromMessage);
        ForwardCommand = new AsyncRelayCommand<MailMessageContent>(StartForwardAsync, CanPrepareFromMessage);
        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        CancelCommand = new AsyncRelayCommand(CancelAsync, CanCancel);
        DiscardDraftCommand = new AsyncRelayCommand(DiscardDraftAsync, CanDiscardDraft);
        RetryDraftSaveCommand = new AsyncRelayCommand(RetryDraftSaveAsync, CanRetryDraftSave);
        AttachFilesCommand = new RelayCommand(AttachFiles, CanAttachFiles);
        RemoveAttachmentCommand = new RelayCommand<MailComposeAttachmentItem>(RemoveAttachment, CanRemoveAttachment);
    }

    public event EventHandler<MailSentEventArgs>? Sent;
    public event EventHandler<GmailDraftChangedEventArgs>? GmailDraftChanged;

    public IRelayCommand NewMessageCommand { get; }
    public IRelayCommand RevealCopyFieldsCommand { get; }
    public IAsyncRelayCommand<MailMessageContent> ReplyCommand { get; }
    public IAsyncRelayCommand<MailMessageContent> ReplyAllCommand { get; }
    public IAsyncRelayCommand<MailMessageContent> ForwardCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand DiscardDraftCommand { get; }
    public IAsyncRelayCommand RetryDraftSaveCommand { get; }
    public IRelayCommand AttachFilesCommand { get; }
    public IRelayCommand<MailComposeAttachmentItem> RemoveAttachmentCommand { get; }

    public MailAccount? ActiveAccount
    {
        get => _activeAccount;
        private set
        {
            if (SetProperty(ref _activeAccount, value))
            {
                OnPropertyChanged(nameof(FromAddress));
                OnPropertyChanged(nameof(IsReplyAllAvailable));
                OnPropertyChanged(nameof(IsGmailServerDraft));
                OnPropertyChanged(nameof(CancelButtonText));
                OnPropertyChanged(nameof(RequiresGmailReauthentication));
                NotifyOpenState();
                NotifyCommandStates();
                ApplyDraftSaveStatus();
            }
        }
    }

    public MailComposeDraft? Draft
    {
        get => _draft;
        private set
        {
            if (SetProperty(ref _draft, value))
            {
                NotifyOpenState();
                NotifyCommandStates();
                ApplyDraftSaveStatus();
            }
        }
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                OnPropertyChanged(nameof(SendButtonText));
                OnPropertyChanged(nameof(CanEdit));
                NotifyCommandStates();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(RequiresGmailReauthentication));
            }
        }
    }

    public MailSendFailureKind? FailureKind
    {
        get => _failureKind;
        private set
        {
            if (SetProperty(ref _failureKind, value))
            {
                OnPropertyChanged(nameof(RequiresGmailReauthentication));
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool IsOpen => Draft is not null && ActiveAccount is not null;
    public bool IsClosed => !IsOpen;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasDraftSaveStatus => !string.IsNullOrWhiteSpace(DraftSaveStatusText);
    public bool HasDraftSaveError =>
        string.Equals(DraftSaveStatusText, "Не удалось сохранить", StringComparison.Ordinal);
    public bool RequiresGmailReauthentication =>
        ActiveAccount?.Provider is MailProviderType.Gmail
        && FailureKind is MailSendFailureKind.ReauthorizationRequired
        && HasError;
    public bool CanEdit => IsOpen && !IsSending && Draft?.IsReadOnly != true;
    public string FromAddress => ActiveAccount?.EmailAddress ?? string.Empty;
    public bool IsReplyAllAvailable => ActiveAccount?.Provider is MailProviderType.Gmail;
    public bool IsGmailServerDraft =>
        ActiveAccount?.Provider is MailProviderType.Gmail && _gmailDraftService is not null;
    public bool IsDraftReadOnly => Draft?.IsReadOnly == true;
    public string CancelButtonText => IsGmailServerDraft ? "Закрыть" : "Отмена";
    public string SendButtonText => IsSending ? "Отправляем…" : "Отправить";
    internal int DraftCount => _drafts.Count;
    internal Task CurrentDraftAutosaveTask =>
        ActiveAccount is MailAccount account && _gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? state)
            ? state.PendingTask
            : Task.CompletedTask;

    public void ActivateAccount(MailAccount? account)
    {
        ThrowIfDisposed();
        FailureKind = null;
        ErrorMessage = null;
        StatusMessage = null;
        ActiveAccount = account is { IsEnabled: true } ? account : null;
        Draft = ActiveAccount is not null && _drafts.TryGetValue(ActiveAccount.Id, out MailComposeDraft? draft)
            ? draft
            : null;
        ApplyDraftSaveStatus();
    }

    public string? DraftSaveStatusText
    {
        get => _draftSaveStatusText;
        private set
        {
            if (SetProperty(ref _draftSaveStatusText, value))
            {
                OnPropertyChanged(nameof(HasDraftSaveStatus));
                OnPropertyChanged(nameof(HasDraftSaveError));
            }
        }
    }

    public void RemoveAccount(Guid accountId)
    {
        RemoveDraftSession(accountId);
        if (ActiveAccount?.Id == accountId)
        {
            ActiveAccount = null;
            Draft = null;
        }
    }

    private void StartNewMessage()
    {
        if (ActiveAccount is not MailAccount account)
        {
            return;
        }

        StatusMessage = null;
        FailureKind = null;
        ErrorMessage = null;
        if (!_drafts.TryGetValue(account.Id, out MailComposeDraft? draft))
        {
            draft = new MailComposeDraft(new MailComposeTemplate(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty), account.Id);
            InstallDraft(account, draft, identity: null, initiallyDirty: false);
        }

        Draft = draft;
    }

    private void RevealCopyFields()
    {
        if (Draft is not null)
        {
            Draft.AreCopyFieldsVisible = true;
            RevealCopyFieldsCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task StartReplyAsync(MailMessageContent? source)
    {
        if (source is not null)
        {
            await ReplaceWithTemplateAsync(_preparationService.CreateReply(source));
        }
    }

    private async Task StartForwardAsync(MailMessageContent? source)
    {
        if (source is not null)
        {
            await ReplaceWithTemplateAsync(_preparationService.CreateForward(source));
        }
    }

    private async Task ReplaceWithTemplateAsync(MailComposeTemplate template)
    {
        if (ActiveAccount is not MailAccount account || IsSending)
        {
            return;
        }

        if (Draft?.HasUserContent == true)
        {
            if (IsGmailServerDraft)
            {
                if (!await CloseGmailDraftAsync(keepComposeOpenOnFailure: true))
                {
                    return;
                }
            }
            else if (!await _confirmationService.ConfirmDiscardAsync(_lifetimeCancellation.Token))
            {
                return;
            }
        }

        MailComposeDraft draft = new(template, account.Id);
        InstallDraft(account, draft, identity: null, initiallyDirty: draft.HasUserContent);
        FailureKind = null;
        ErrorMessage = null;
        StatusMessage = null;
    }

    private async Task SendAsync()
    {
        if (!CanSend())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _sendGate, 1, 0) != 0)
        {
            return;
        }

        IsSending = true;
        FailureKind = null;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            if (ActiveAccount is not MailAccount account || Draft is not MailComposeDraft draft)
            {
                return;
            }

            MailComposeInput input = draft.Snapshot();
            MailComposeRequest request;
            try
            {
                request = _requestFactory.Create(account, input);
            }
            catch (MailComposeValidationException exception)
            {
                FailureKind = MailSendFailureKind.InvalidRequest;
                ErrorMessage = exception.UserMessage;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Subject)
                && string.IsNullOrWhiteSpace(request.TextBody)
                && request.Attachments.Count == 0
                && !await _confirmationService.ConfirmEmptyMessageAsync(_lifetimeCancellation.Token))
            {
                return;
            }

            MailSendResult result;
            try
            {
                if (account.Provider is MailProviderType.Gmail
                    && _gmailDraftService is not null
                    && _gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? gmailState))
                {
                    gmailState.CancelDebounce();
                    if (!await SaveLatestGmailDraftAsync(gmailState, force: false, _lifetimeCancellation.Token)
                        || gmailState.Identity is not GmailDraftIdentity identity)
                    {
                        return;
                    }

                    result = await _gmailDraftService.SendAsync(account, identity, _lifetimeCancellation.Token);
                }
                else
                {
                    IMailSendProvider provider = _providerFactory.Get(account.Provider);
                    result = await provider.SendAsync(account, request, _lifetimeCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
            {
                result = MailSendResult.Failure(
                    MailSendFailureKind.CapabilityUnavailable,
                    "Отправка для этого почтового аккаунта недоступна.");
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or FormatException)
            {
                result = MailSendResult.Failure(
                    MailSendFailureKind.ConnectionFailed,
                    "Не удалось отправить письмо. Проверьте подключение и повторите попытку.");
            }

            if (!result.IsMessageSent)
            {
                FailureKind = result.FailureKind;
                ErrorMessage = result.UserMessage;
                return;
            }

            bool sentServerDraft = _gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? sentState)
                && sentState.Identity is not null;
            RemoveDraftSession(account.Id);
            if (ActiveAccount?.Id == account.Id)
            {
                Draft = null;
                StatusMessage = result.UserMessage;
            }
            if (sentServerDraft)
            {
                GmailDraftChanged?.Invoke(
                    this,
                    new GmailDraftChangedEventArgs(account.Id, GmailDraftChangeKind.Sent));
            }
            Sent?.Invoke(this, new MailSentEventArgs(account.Id, result.SentCopySaved));
        }
        finally
        {
            IsSending = false;
            Interlocked.Exchange(ref _sendGate, 0);
        }
    }

    private void AttachFiles()
    {
        if (Draft is not MailComposeDraft draft || _attachmentDialogService is null || IsSending)
        {
            return;
        }

        try
        {
            IReadOnlyList<OutgoingMailAttachment> attachments = _attachmentDialogService.SelectOutgoingAttachments();
            draft.AddLocalAttachments(attachments);
            FailureKind = null;
            ErrorMessage = null;
        }
        catch (MailAttachmentException exception)
        {
            FailureKind = exception.FailureKind is MailAttachmentFailureKind.MessageTooLarge
                ? MailSendFailureKind.MessageTooLarge
                : MailSendFailureKind.AttachmentUnavailable;
            ErrorMessage = exception.UserMessage;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            FailureKind = MailSendFailureKind.AttachmentUnavailable;
            ErrorMessage = "Не удалось прочитать выбранный файл.";
        }
    }

    private void RemoveAttachment(MailComposeAttachmentItem? item)
    {
        if (Draft is not null && item is not null && !IsSending)
        {
            Draft.RemoveAttachment(item);
        }
    }

    private async Task CancelAsync()
    {
        if (ActiveAccount is not MailAccount account || Draft is not MailComposeDraft draft || IsSending)
        {
            return;
        }

        if (account.Provider is MailProviderType.Gmail
            && _gmailDraftService is not null
            && _gmailDraftStates.ContainsKey(account.Id))
        {
            await CloseGmailDraftAsync(keepComposeOpenOnFailure: true);
            return;
        }

        if (draft.HasUserContent
            && !await _confirmationService.ConfirmDiscardAsync(_lifetimeCancellation.Token))
        {
            return;
        }

        _drafts.Remove(account.Id);
        Draft = null;
        FailureKind = null;
        ErrorMessage = null;
    }

    private async Task DiscardDraftAsync()
    {
        if (ActiveAccount is not { Provider: MailProviderType.Gmail } account
            || Draft is not MailComposeDraft draft
            || _gmailDraftService is null
            || !_gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? state)
            || IsSending)
        {
            return;
        }

        if ((draft.HasUserContent || state.Identity is not null)
            && !await _confirmationService.ConfirmDiscardAsync(_lifetimeCancellation.Token))
        {
            return;
        }

        IsSending = true;
        state.CancelDebounce();
        try
        {
            await state.Gate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (state.Identity is null && state.RequiresExplicitRetry)
                {
                    throw new GmailDraftException(
                        MailSendFailureKind.Ambiguous,
                        "Lantern не может подтвердить создание черновика. Проверьте папку «Черновики» перед удалением.");
                }

                if (state.Identity is GmailDraftIdentity identity)
                {
                    await _gmailDraftService.DeleteAsync(account, identity, _lifetimeCancellation.Token);
                    GmailDraftChanged?.Invoke(
                        this,
                        new GmailDraftChangedEventArgs(account.Id, GmailDraftChangeKind.Deleted));
                }

                RemoveDraftSession(account.Id);
                Draft = null;
                FailureKind = null;
                ErrorMessage = null;
                DraftSaveStatusText = null;
            }
            finally
            {
                state.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (GmailDraftException exception)
        {
            FailureKind = exception.FailureKind;
            ErrorMessage = exception.UserMessage;
            SetDraftSaveStatus(
                state,
                state.Identity is null && state.RequiresExplicitRetry
                    ? "Не удалось сохранить"
                    : "Не удалось удалить черновик");
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task RetryDraftSaveAsync()
    {
        if (ActiveAccount is MailAccount account
            && _gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? state))
        {
            state.CancelDebounce();
            state.RequiresExplicitRetry = false;
            await SaveLatestGmailDraftAsync(state, force: false, _lifetimeCancellation.Token);
        }
    }

    internal async Task<bool> OpenGmailDraftAsync(MailAccount account, string draftId)
    {
        ThrowIfDisposed();
        if (_gmailDraftService is null
            || account.Provider is not MailProviderType.Gmail
            || ActiveAccount?.Id != account.Id
            || string.IsNullOrWhiteSpace(draftId)
            || IsSending)
        {
            return false;
        }

        if (_gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? existing)
            && string.Equals(existing.Identity?.DraftId, draftId, StringComparison.Ordinal))
        {
            Draft = existing.Draft;
            ApplyDraftSaveStatus();
            return true;
        }

        IsSending = true;
        FailureKind = null;
        ErrorMessage = null;
        try
        {
            GmailDraftLoadResult loaded = await _gmailDraftService.LoadAsync(
                account,
                draftId,
                _lifetimeCancellation.Token);
            if (ActiveAccount?.Id != account.Id || _disposed)
            {
                return false;
            }

            MailComposeDraft draft = new(loaded.Template, account.Id);
            InstallDraft(account, draft, loaded.Identity, initiallyDirty: false);
            SetDraftSaveStatus(
                _gmailDraftStates[account.Id],
                loaded.IsReadOnly ? loaded.RestrictionMessage : "Сохранено");
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (GmailDraftException exception)
        {
            FailureKind = exception.FailureKind;
            ErrorMessage = exception.UserMessage;
            return false;
        }
        finally
        {
            IsSending = false;
        }
    }

    public async Task FlushPendingGmailDraftsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        timeout.CancelAfter(FinalAutosaveTimeout);
        foreach (GmailComposeDraftState state in _gmailDraftStates.Values.ToArray())
        {
            state.CancelDebounce();
            if (!state.IsDirty || state.IsReadOnly || state.IsTerminal)
            {
                continue;
            }

            try
            {
                await SaveLatestGmailDraftAsync(state, force: false, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void InstallDraft(
        MailAccount account,
        MailComposeDraft draft,
        GmailDraftIdentity? identity,
        bool initiallyDirty)
    {
        RemoveDraftSession(account.Id);
        _drafts[account.Id] = draft;
        if (account.Provider is MailProviderType.Gmail && _gmailDraftService is not null)
        {
            GmailComposeDraftState state = new(account, draft, identity);
            if (initiallyDirty)
            {
                state.MarkDirty();
            }

            _gmailDraftStates[account.Id] = state;
            draft.Changed += OnDraftChanged;
            if (initiallyDirty)
            {
                ScheduleGmailDraftAutosave(state);
            }
        }

        Draft = draft;
        ApplyDraftSaveStatus();
    }

    private void OnDraftChanged(object? sender, EventArgs eventArgs)
    {
        GmailComposeDraftState? state = _gmailDraftStates.Values.FirstOrDefault(item =>
            ReferenceEquals(item.Draft, sender));
        if (state is null || state.IsTerminal || state.IsReadOnly || IsSending)
        {
            return;
        }

        state.MarkDirty();
        if (state.RequiresExplicitRetry)
        {
            return;
        }

        SetDraftSaveStatus(state, null);
        ScheduleGmailDraftAutosave(state);
    }

    private void ScheduleGmailDraftAutosave(GmailComposeDraftState state)
    {
        state.CancelDebounce();
        if (state.IsTerminal || state.IsReadOnly)
        {
            return;
        }

        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            state.Lifetime.Token,
            _lifetimeCancellation.Token);
        state.DebounceCancellation = cancellation;
        state.PendingTask = DebouncedSaveAsync(state, cancellation);
    }

    private async Task DebouncedSaveAsync(
        GmailComposeDraftState state,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _draftAutosaveScheduler.DelayAsync(GmailDraftAutosaveDelay, cancellation.Token);
            await SaveLatestGmailDraftAsync(state, force: false, state.Lifetime.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || state.Lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(state.DebounceCancellation, cancellation))
            {
                state.DebounceCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<bool> SaveLatestGmailDraftAsync(
        GmailComposeDraftState state,
        bool force,
        CancellationToken cancellationToken)
    {
        if (_gmailDraftService is null
            || state.IsTerminal
            || state.IsReadOnly
            || state.RequiresExplicitRetry)
        {
            return state.IsReadOnly;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            while (!state.IsTerminal)
            {
                long generation = state.Generation;
                if (!force && generation <= state.SavedGeneration)
                {
                    return true;
                }

                if (state.Identity is null && !state.Draft.HasUserContent)
                {
                    state.MarkSaved(generation);
                    SetDraftSaveStatus(state, null);
                    return true;
                }

                SetDraftSaveStatus(state, "Сохранение…");
                MailComposeRequest request;
                try
                {
                    request = _requestFactory.CreateDraft(state.Account, state.Draft.Snapshot());
                }
                catch (MailComposeValidationException exception)
                {
                    FailureKind = MailSendFailureKind.InvalidRequest;
                    ErrorMessage = exception.UserMessage;
                    SetDraftSaveStatus(state, "Не удалось сохранить");
                    return false;
                }

                bool wasNew = state.Identity is null;
                try
                {
                    GmailDraftIdentity saved = await _gmailDraftService.SaveAsync(
                        state.Account,
                        state.Identity,
                        request,
                        cancellationToken).WaitAsync(cancellationToken);
                    if (state.IsTerminal)
                    {
                        return false;
                    }

                    state.Identity = saved;
                    state.RequiresExplicitRetry = false;
                    state.MarkSaved(generation);
                    GmailDraftChanged?.Invoke(
                        this,
                        new GmailDraftChangedEventArgs(
                            state.Account.Id,
                            wasNew ? GmailDraftChangeKind.Created : GmailDraftChangeKind.Updated));
                    FailureKind = null;
                    ErrorMessage = null;
                    if (state.Generation <= generation)
                    {
                        SetDraftSaveStatus(state, "Сохранено");
                        return true;
                    }

                    force = true;
                }
                catch (GmailDraftException exception)
                {
                    if (exception.FailureKind is MailSendFailureKind.Ambiguous && state.Identity is null)
                    {
                        state.RequiresExplicitRetry = true;
                    }

                    if (exception.FailureKind is MailSendFailureKind.ReauthorizationRequired
                        or MailSendFailureKind.Ambiguous
                        or MailSendFailureKind.AttachmentUnavailable
                        or MailSendFailureKind.MessageTooLarge
                        or MailSendFailureKind.InvalidRequest)
                    {
                        FailureKind = exception.FailureKind;
                        ErrorMessage = exception.UserMessage;
                    }

                    SetDraftSaveStatus(state, "Не удалось сохранить");
                    return false;
                }
                catch (MailComposeValidationException exception)
                {
                    FailureKind = MailSendFailureKind.InvalidRequest;
                    ErrorMessage = exception.UserMessage;
                    SetDraftSaveStatus(state, "Не удалось сохранить");
                    return false;
                }
            }

            return false;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<bool> CloseGmailDraftAsync(bool keepComposeOpenOnFailure)
    {
        if (ActiveAccount is not MailAccount account
            || !_gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? state))
        {
            return false;
        }

        state.CancelDebounce();
        if (!state.IsReadOnly && state.IsDirty)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            timeout.CancelAfter(FinalAutosaveTimeout);
            bool saved;
            try
            {
                saved = await SaveLatestGmailDraftAsync(state, force: false, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (state.Identity is null)
                {
                    state.RequiresExplicitRetry = true;
                }

                SetDraftSaveStatus(state, "Не удалось сохранить");
                saved = false;
            }

            if (!saved && keepComposeOpenOnFailure)
            {
                return false;
            }
        }

        RemoveDraftSession(account.Id);
        Draft = null;
        FailureKind = null;
        ErrorMessage = null;
        DraftSaveStatusText = null;
        return true;
    }

    private void RemoveDraftSession(Guid accountId)
    {
        if (_gmailDraftStates.Remove(accountId, out GmailComposeDraftState? state))
        {
            state.Draft.Changed -= OnDraftChanged;
            state.Complete();
        }

        _drafts.Remove(accountId);
    }

    private void SetDraftSaveStatus(GmailComposeDraftState state, string? value)
    {
        state.SaveStatus = value;
        if (ActiveAccount?.Id == state.Account.Id && ReferenceEquals(Draft, state.Draft))
        {
            DraftSaveStatusText = value;
            RetryDraftSaveCommand.NotifyCanExecuteChanged();
        }
    }

    private void ApplyDraftSaveStatus()
    {
        DraftSaveStatusText = ActiveAccount is MailAccount account
            && _gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? state)
            && ReferenceEquals(Draft, state.Draft)
                ? state.SaveStatus
                : null;
    }

    internal void ClearGmailReauthenticationError(Guid accountId)
    {
        if (ActiveAccount?.Id == accountId && RequiresGmailReauthentication)
        {
            FailureKind = null;
            ErrorMessage = null;
            if (_gmailDraftStates.TryGetValue(accountId, out GmailComposeDraftState? state) && state.IsDirty)
            {
                ScheduleGmailDraftAutosave(state);
            }
        }
    }

    private async Task StartReplyAllAsync(MailMessageContent? source)
    {
        if (source is not null && ActiveAccount is MailAccount account)
        {
            MailComposeTemplate template = _preparationService.CreateReplyAll(source, account);
            if (!string.IsNullOrWhiteSpace(template.To) || !string.IsNullOrWhiteSpace(template.Cc))
            {
                await ReplaceWithTemplateAsync(template);
            }
        }
    }

    private bool CanStartNewMessage() => ActiveAccount is not null && !IsSending;
    private bool CanRevealCopyFields() => Draft is { AreCopyFieldsVisible: false } && !IsSending;
    private bool CanPrepareFromMessage(MailMessageContent? source) => ActiveAccount is not null && source is not null && !IsSending;
    private bool CanPrepareReplyAllFromMessage(MailMessageContent? source) =>
        IsReplyAllAvailable && source is not null && !IsSending;
    private bool CanSend() => IsOpen && !IsSending && Draft?.IsReadOnly != true;
    private bool CanCancel() => IsOpen && !IsSending;
    private bool CanDiscardDraft() => IsGmailServerDraft && IsOpen && !IsSending;
    private bool CanRetryDraftSave() =>
        IsGmailServerDraft
        && IsOpen
        && !IsSending
        && ActiveAccount is MailAccount account
        && _gmailDraftStates.TryGetValue(account.Id, out GmailComposeDraftState? state)
        && state.IsDirty
        && string.Equals(state.SaveStatus, "Не удалось сохранить", StringComparison.Ordinal);
    private bool CanAttachFiles() => CanEdit && _attachmentDialogService is not null;
    private bool CanRemoveAttachment(MailComposeAttachmentItem? item) => CanEdit && item is not null;

    private void NotifyCommandStates()
    {
        NewMessageCommand.NotifyCanExecuteChanged();
        RevealCopyFieldsCommand.NotifyCanExecuteChanged();
        ReplyCommand.NotifyCanExecuteChanged();
        ReplyAllCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DiscardDraftCommand.NotifyCanExecuteChanged();
        RetryDraftSaveCommand.NotifyCanExecuteChanged();
        AttachFilesCommand.NotifyCanExecuteChanged();
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
    }

    private void NotifyOpenState()
    {
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(IsClosed));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsDraftReadOnly));
        OnPropertyChanged(nameof(IsGmailServerDraft));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        foreach (GmailComposeDraftState state in _gmailDraftStates.Values)
        {
            state.Draft.Changed -= OnDraftChanged;
            state.Complete();
        }

        _gmailDraftStates.Clear();
        _drafts.Clear();
        Draft = null;
        ActiveAccount = null;
        _lifetimeCancellation.Dispose();
    }

    internal static MailComposeViewModel CreateUnavailable() =>
        new(
            new UnavailableSendProviderFactory(),
            new MailComposeRequestFactory(),
            new MailComposePreparationService(),
            new AlwaysConfirmComposeService());

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class GmailComposeDraftState(
        MailAccount account,
        MailComposeDraft draft,
        GmailDraftIdentity? identity)
    {
        public MailAccount Account { get; } = account;
        public MailComposeDraft Draft { get; } = draft;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Lifetime { get; } = new();
        public GmailDraftIdentity? Identity { get; set; } = identity;
        public long Generation { get; private set; }
        public long SavedGeneration { get; private set; }
        public string? SaveStatus { get; set; }
        public CancellationTokenSource? DebounceCancellation { get; set; }
        public Task PendingTask { get; set; } = Task.CompletedTask;
        public bool IsReadOnly => Draft.IsReadOnly;
        public bool IsTerminal { get; private set; }
        public bool RequiresExplicitRetry { get; set; }
        public bool IsDirty => Generation > SavedGeneration;

        public void MarkDirty() => Generation++;

        public void MarkSaved(long generation) =>
            SavedGeneration = Math.Max(SavedGeneration, generation);

        public void CancelDebounce()
        {
            CancellationTokenSource? cancellation = DebounceCancellation;
            DebounceCancellation = null;
            cancellation?.Cancel();
        }

        public void Cancel()
        {
            CancelDebounce();
            Lifetime.Cancel();
        }

        public void Complete()
        {
            if (IsTerminal)
            {
                return;
            }

            IsTerminal = true;
            Cancel();
        }
    }

    private sealed class UnavailableSendProviderFactory : IMailSendProviderFactory
    {
        public IMailSendProvider Get(MailProviderType providerType) =>
            throw new KeyNotFoundException("No send provider is configured.");
    }

    private sealed class AlwaysConfirmComposeService : IMailComposeConfirmationService
    {
        public Task<bool> ConfirmEmptyMessageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
