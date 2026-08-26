using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using UnifiedMessenger.App.Models;
using UnifiedMessenger.App.Services.WebView;

namespace UnifiedMessenger.App.Services.Mail;

public sealed class MailMessageHtmlRenderer(
    ICoreWebView2EnvironmentProvider environmentProvider,
    IMailHtmlDocumentBuilder documentBuilder,
    MailRendererNavigationPolicy navigationPolicy,
    MailRendererNavigationCoordinator navigationCoordinator) : IMailMessageHtmlRenderer
{
    public const int MaximumControllerCount = 1;
    internal const CoreWebView2PrintDialogKind PrintDialogKind = CoreWebView2PrintDialogKind.System;
    public const bool JavaScriptEnabled = false;
    public const bool WebMessagingEnabled = false;
    public const bool HostObjectsEnabled = false;
    public const bool UsesInPrivateProfile = true;
    internal const string ProfileName = "mail-message-renderer";

    private const string EmptyDocument =
        "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" " +
        "content=\"default-src 'none'; script-src 'none'; connect-src 'none'; frame-src 'none'; object-src 'none'; " +
        "form-action 'none'; base-uri 'none'\"></head><body></body></html>";

    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _coreWebView;
    private Task? _initializationTask;
    private IntPtr _parentWindow;
    private string _currentDocument = EmptyDocument;
    private Rectangle _currentBounds;
    private bool _hasCurrentBounds;
    private bool _isVisible;
    private bool _isDocumentReady;
    private bool _lastCanPrint;
    private bool _shutdownStarted;
    private bool _disposed;
    private long _controllerGeneration;

    public bool IsInitialized => _controller is not null && _coreWebView is not null;
    public bool IsVisible => IsInitialized && _isVisible;
    public bool CanPrint => IsVisible && _isDocumentReady && !_shutdownStarted;
    public int ControllerCount => IsInitialized ? 1 : 0;
    public event EventHandler? PrintAvailabilityChanged;

    public async Task ShowAsync(
        IntPtr parentWindow,
        Rectangle bounds,
        MailMessageContent content,
        IReadOnlyDictionary<string, MailImageContent>? remoteImages,
        bool isVisible,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        ValidateBounds(bounds);
        if (parentWindow == IntPtr.Zero)
        {
            throw new ArgumentException("A real MainWindow HWND is required.", nameof(parentWindow));
        }

        if (content.BodyKind is not MailMessageBodyKind.SanitizedHtml)
        {
            throw new ArgumentException("The mail renderer accepts only sanitized HTML content.", nameof(content));
        }

        if (_shutdownStarted)
        {
            return;
        }

        long generation = _controllerGeneration;
        _initializationTask ??= InitializeAsync(parentWindow, generation);
        await _initializationTask.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_shutdownStarted
            || generation != _controllerGeneration
            || _controller is null
            || _coreWebView is null)
        {
            return;
        }

        _currentDocument = documentBuilder.Build(content, remoteImages);
        SetBounds(bounds);
        SetVisibility(isVisible: false);
        SetDocumentReady(isReady: false);
        _coreWebView.Navigate(MailRendererNavigationPolicy.InternalDocumentUri.AbsoluteUri);
        SetVisibility(isVisible);
    }

    public void UpdateLayout(Rectangle bounds, bool isVisible)
    {
        ValidateBounds(bounds);
        if (_shutdownStarted || _controller is null)
        {
            return;
        }

        if (!isVisible)
        {
            Hide(clearContent: false);
            return;
        }

        SetBounds(bounds);
        SetVisibility(isVisible: true);
    }

    public void NotifyParentWindowPositionChanged()
    {
        if (_shutdownStarted || !IsVisible)
        {
            return;
        }

        try
        {
            if (_controller is null)
            {
                return;
            }

            _controller.NotifyParentWindowPositionChanged();
        }
        catch (COMException)
        {
            Hide(clearContent: true);
        }
    }

    public bool TryShowPrintPreview()
    {
        if (!CanPrint || _coreWebView is null)
        {
            return false;
        }

        try
        {
            _coreWebView.ShowPrintUI(PrintDialogKind);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            return false;
        }
    }

    public void Hide(bool clearContent)
    {
        SetVisibility(isVisible: false);

        if (!clearContent)
        {
            return;
        }

        _currentDocument = EmptyDocument;
        try
        {
            _coreWebView?.Navigate(MailRendererNavigationPolicy.InternalDocumentUri.AbsoluteUri);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            // A closing or failed controller no longer has content that can become visible.
        }
    }

    public void ReleaseController(bool clearContent)
    {
        _controllerGeneration++;
        if (clearContent)
        {
            _currentDocument = EmptyDocument;
        }

        Unsubscribe();
        try
        {
            _controller?.Close();
        }
        catch (COMException)
        {
            // The WebView2 process already released the controller.
        }

        _coreWebView = null;
        _controller = null;
        _environment = null;
        _initializationTask = null;
        _hasCurrentBounds = false;
        _isVisible = false;
        _isDocumentReady = false;
        RaisePrintAvailabilityChangedIfNeeded();
    }

    public void BeginShutdown()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        ReleaseController(clearContent: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        BeginShutdown();
        _initializationGate.Dispose();
    }

    private async Task InitializeAsync(IntPtr parentWindow, long generation)
    {
        await _initializationGate.WaitAsync();
        try
        {
            if (_shutdownStarted || generation != _controllerGeneration || IsInitialized)
            {
                return;
            }

            if (_parentWindow != IntPtr.Zero && _parentWindow != parentWindow)
            {
                throw new InvalidOperationException("The mail renderer cannot switch its MainWindow parent.");
            }

            _parentWindow = parentWindow;
            CoreWebView2Environment environment = await environmentProvider.GetAsync();
            if (_shutdownStarted || generation != _controllerGeneration)
            {
                return;
            }

            CoreWebView2ControllerOptions options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = ProfileName;
            options.IsInPrivateModeEnabled = UsesInPrivateProfile;
            CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(
                parentWindow,
                options);
            if (_shutdownStarted || generation != _controllerGeneration)
            {
                controller.Close();
                return;
            }

            _environment = environment;
            _controller = controller;
            _coreWebView = controller.CoreWebView2;
            controller.IsVisible = false;
            _isVisible = false;
            controller.DefaultBackgroundColor = Color.White;
            ConfigureCoreWebView(_coreWebView);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private void ConfigureCoreWebView(CoreWebView2 coreWebView)
    {
        CoreWebView2Settings settings = coreWebView.Settings;
        settings.IsScriptEnabled = JavaScriptEnabled;
        settings.IsWebMessageEnabled = WebMessagingEnabled;
        settings.AreHostObjectsAllowed = HostObjectsEnabled;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = true;
        settings.IsZoomControlEnabled = true;
        coreWebView.AddWebResourceRequestedFilter(
            "*",
            CoreWebView2WebResourceContext.All,
            CoreWebView2WebResourceRequestSourceKinds.All);
        coreWebView.WebResourceRequested += OnWebResourceRequested;
        coreWebView.NavigationCompleted += OnNavigationCompleted;
        coreWebView.NavigationStarting += OnNavigationStarting;
        coreWebView.NewWindowRequested += OnNewWindowRequested;
        coreWebView.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        if (_environment is null
            || !Uri.TryCreate(eventArgs.Request.Uri, UriKind.Absolute, out Uri? target))
        {
            return;
        }

        bool isInternalDocument = navigationPolicy.IsInternalDocument(target)
            && eventArgs.ResourceContext is CoreWebView2WebResourceContext.Document;
        if (!isInternalDocument
            && navigationPolicy.IsAllowedInRendererResource(target, eventArgs.ResourceContext))
        {
            // Validated CID/remote bytes are embedded as data images and require no network response.
            return;
        }

        byte[] payload = Encoding.UTF8.GetBytes(isInternalDocument ? _currentDocument : string.Empty);
        eventArgs.Response = _environment.CreateWebResourceResponse(
            new MemoryStream(payload, writable: false),
            isInternalDocument ? 200 : 403,
            isInternalDocument ? "OK" : "Blocked",
            isInternalDocument
                ? "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nPragma: no-cache\r\nX-Content-Type-Options: nosniff"
                : "Content-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out Uri? target))
        {
            eventArgs.Cancel = true;
            return;
        }

        MailRendererNavigationDisposition disposition = navigationCoordinator.RouteTopLevel(
            target,
            eventArgs.IsUserInitiated);
        eventArgs.Cancel = disposition is not MailRendererNavigationDisposition.InternalDocument;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        bool isInternalDocument = _coreWebView is CoreWebView2 coreWebView
            && Uri.TryCreate(coreWebView.Source, UriKind.Absolute, out Uri? source)
            && navigationPolicy.IsInternalDocument(source);
        SetDocumentReady(eventArgs.IsSuccess && isInternalDocument);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out Uri? target))
        {
            _ = navigationCoordinator.RouteTopLevel(target, eventArgs.IsUserInitiated);
        }
    }

    private static void OnLaunchingExternalUriScheme(
        object? sender,
        CoreWebView2LaunchingExternalUriSchemeEventArgs eventArgs) =>
        eventArgs.Cancel = true;

    private void Unsubscribe()
    {
        if (_coreWebView is not CoreWebView2 coreWebView)
        {
            return;
        }

        try
        {
            coreWebView.WebResourceRequested -= OnWebResourceRequested;
            coreWebView.NavigationCompleted -= OnNavigationCompleted;
            coreWebView.NavigationStarting -= OnNavigationStarting;
            coreWebView.NewWindowRequested -= OnNewWindowRequested;
            coreWebView.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            coreWebView.RemoveWebResourceRequestedFilter(
                "*",
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or COMException)
        {
            // A disposed controller no longer accepts event/filter changes.
        }
    }

    private static void ValidateBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "The mail renderer bounds must be positive.");
        }
    }

    private void SetBounds(Rectangle bounds)
    {
        if (_controller is null || _hasCurrentBounds && _currentBounds == bounds)
        {
            return;
        }

        _controller.Bounds = bounds;
        _currentBounds = bounds;
        _hasCurrentBounds = true;
    }

    private void SetVisibility(bool isVisible)
    {
        if (_controller is null || _isVisible == isVisible)
        {
            return;
        }

        _controller.IsVisible = isVisible;
        _isVisible = isVisible;
        RaisePrintAvailabilityChangedIfNeeded();
    }

    private void SetDocumentReady(bool isReady)
    {
        if (_isDocumentReady == isReady)
        {
            return;
        }

        _isDocumentReady = isReady;
        RaisePrintAvailabilityChangedIfNeeded();
    }

    private void RaisePrintAvailabilityChangedIfNeeded()
    {
        bool canPrint = CanPrint;
        if (_lastCanPrint == canPrint)
        {
            return;
        }

        _lastCanPrint = canPrint;
        PrintAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
