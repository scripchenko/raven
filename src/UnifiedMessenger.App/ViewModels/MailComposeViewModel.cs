using System.IO;
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

    internal MailComposeDraft(MailComposeTemplate template)
    {
        _to = template.To;
        _cc = template.Cc;
        _bcc = template.Bcc;
        _subject = template.Subject;
        _textBody = template.TextBody;
        _areCopyFieldsVisible = !string.IsNullOrWhiteSpace(_cc) || !string.IsNullOrWhiteSpace(_bcc);
        ReplyContext = template.ReplyContext;
    }

    public string To
    {
        get => _to;
        set => SetProperty(ref _to, value ?? string.Empty);
    }

    public string Cc
    {
        get => _cc;
        set => SetProperty(ref _cc, value ?? string.Empty);
    }

    public string Bcc
    {
        get => _bcc;
        set => SetProperty(ref _bcc, value ?? string.Empty);
    }

    public string Subject
    {
        get => _subject;
        set => SetProperty(ref _subject, value ?? string.Empty);
    }

    public string TextBody
    {
        get => _textBody;
        set => SetProperty(ref _textBody, value ?? string.Empty);
    }

    public bool AreCopyFieldsVisible
    {
        get => _areCopyFieldsVisible;
        set => SetProperty(ref _areCopyFieldsVisible, value);
    }

    internal MailReplyContext? ReplyContext { get; }

    public bool HasUserContent =>
        !string.IsNullOrWhiteSpace(To)
        || !string.IsNullOrWhiteSpace(Cc)
        || !string.IsNullOrWhiteSpace(Bcc)
        || !string.IsNullOrWhiteSpace(Subject)
        || !string.IsNullOrWhiteSpace(TextBody);

    internal MailComposeInput Snapshot() =>
        new(To, Cc, Bcc, Subject, TextBody, ReplyContext);
}

public sealed class MailSentEventArgs(Guid accountId, bool sentCopySaved) : EventArgs
{
    public Guid AccountId { get; } = accountId;
    public bool SentCopySaved { get; } = sentCopySaved;
}

public sealed class MailComposeViewModel : ObservableObject, IDisposable
{
    private readonly IMailSendProviderFactory _providerFactory;
    private readonly IMailComposeRequestFactory _requestFactory;
    private readonly IMailComposePreparationService _preparationService;
    private readonly IMailComposeConfirmationService _confirmationService;
    private readonly Dictionary<Guid, MailComposeDraft> _drafts = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private MailAccount? _activeAccount;
    private MailComposeDraft? _draft;
    private bool _isSending;
    private string? _errorMessage;
    private string? _statusMessage;
    private int _sendGate;
    private bool _disposed;

    public MailComposeViewModel(
        IMailSendProviderFactory providerFactory,
        IMailComposeRequestFactory requestFactory,
        IMailComposePreparationService preparationService,
        IMailComposeConfirmationService confirmationService)
    {
        _providerFactory = providerFactory;
        _requestFactory = requestFactory;
        _preparationService = preparationService;
        _confirmationService = confirmationService;
        NewMessageCommand = new RelayCommand(StartNewMessage, CanStartNewMessage);
        RevealCopyFieldsCommand = new RelayCommand(RevealCopyFields, CanRevealCopyFields);
        ReplyCommand = new AsyncRelayCommand<MailMessageContent>(StartReplyAsync, CanPrepareFromMessage);
        ForwardCommand = new AsyncRelayCommand<MailMessageContent>(StartForwardAsync, CanPrepareFromMessage);
        SendCommand = new AsyncRelayCommand(SendAsync, CanSend);
        CancelCommand = new AsyncRelayCommand(CancelAsync, CanCancel);
    }

    public event EventHandler<MailSentEventArgs>? Sent;

    public IRelayCommand NewMessageCommand { get; }
    public IRelayCommand RevealCopyFieldsCommand { get; }
    public IAsyncRelayCommand<MailMessageContent> ReplyCommand { get; }
    public IAsyncRelayCommand<MailMessageContent> ForwardCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand CancelCommand { get; }

    public MailAccount? ActiveAccount
    {
        get => _activeAccount;
        private set
        {
            if (SetProperty(ref _activeAccount, value))
            {
                OnPropertyChanged(nameof(FromAddress));
                NotifyOpenState();
                NotifyCommandStates();
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
    public bool CanEdit => IsOpen && !IsSending;
    public string FromAddress => ActiveAccount?.EmailAddress ?? string.Empty;
    public string SendButtonText => IsSending ? "Отправляем…" : "Отправить";
    internal int DraftCount => _drafts.Count;

    public void ActivateAccount(MailAccount? account)
    {
        ThrowIfDisposed();
        ActiveAccount = account is { IsEnabled: true } ? account : null;
        Draft = ActiveAccount is not null && _drafts.TryGetValue(ActiveAccount.Id, out MailComposeDraft? draft)
            ? draft
            : null;
        ErrorMessage = null;
        StatusMessage = null;
    }

    public void RemoveAccount(Guid accountId)
    {
        _drafts.Remove(accountId);
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
        ErrorMessage = null;
        if (!_drafts.TryGetValue(account.Id, out MailComposeDraft? draft))
        {
            draft = new MailComposeDraft(new MailComposeTemplate(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty));
            _drafts.Add(account.Id, draft);
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

        if (Draft?.HasUserContent == true
            && !await _confirmationService.ConfirmDiscardAsync(_lifetimeCancellation.Token))
        {
            return;
        }

        MailComposeDraft draft = new(template);
        _drafts[account.Id] = draft;
        Draft = draft;
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
                ErrorMessage = exception.UserMessage;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Subject)
                && string.IsNullOrWhiteSpace(request.TextBody)
                && !await _confirmationService.ConfirmEmptyMessageAsync(_lifetimeCancellation.Token))
            {
                return;
            }

            MailSendResult result;
            try
            {
                IMailSendProvider provider = _providerFactory.Get(account.Provider);
                result = await provider.SendAsync(account, request, _lifetimeCancellation.Token);
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
                ErrorMessage = result.UserMessage;
                return;
            }

            _drafts.Remove(account.Id);
            if (ActiveAccount?.Id == account.Id)
            {
                Draft = null;
                StatusMessage = result.UserMessage;
            }
            Sent?.Invoke(this, new MailSentEventArgs(account.Id, result.SentCopySaved));
        }
        finally
        {
            IsSending = false;
            Interlocked.Exchange(ref _sendGate, 0);
        }
    }

    private async Task CancelAsync()
    {
        if (ActiveAccount is not MailAccount account || Draft is not MailComposeDraft draft || IsSending)
        {
            return;
        }

        if (draft.HasUserContent
            && !await _confirmationService.ConfirmDiscardAsync(_lifetimeCancellation.Token))
        {
            return;
        }

        _drafts.Remove(account.Id);
        Draft = null;
        ErrorMessage = null;
    }

    private bool CanStartNewMessage() => ActiveAccount is not null && !IsSending;
    private bool CanRevealCopyFields() => Draft is { AreCopyFieldsVisible: false } && !IsSending;
    private bool CanPrepareFromMessage(MailMessageContent? source) => ActiveAccount is not null && source is not null && !IsSending;
    private bool CanSend() => IsOpen && !IsSending;
    private bool CanCancel() => IsOpen && !IsSending;

    private void NotifyCommandStates()
    {
        NewMessageCommand.NotifyCanExecuteChanged();
        RevealCopyFieldsCommand.NotifyCanExecuteChanged();
        ReplyCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void NotifyOpenState()
    {
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(IsClosed));
        OnPropertyChanged(nameof(CanEdit));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
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
