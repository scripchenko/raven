# raven architecture

raven is a WPF application built on .NET 10, with a separate xUnit test project. Its public name is `raven`; the internal assembly and executable name remain `UnifiedMessenger.App` for compatibility with existing installations.

## Application state and shell

`AppSettings` holds general settings and the account list. `JsonSettingsService` writes them to `%APPDATA%\UnifiedMessenger\settings.json` using atomic replacement. The main window and its ViewModel manage account selection, the Home page, Settings, and mail. A named mutex and local activation channel coordinate a single running instance; a second launch activates the existing window. The tray icon remains available while the main window is hidden.

The installation directory `%LOCALAPPDATA%\Programs\Lantern`, `UnifiedMessenger` data directories, and stable Inno Setup `AppId` are compatibility identifiers. Changing them would require a separate migration of user data and sessions.

## Web messengers

`BuiltInServiceCatalog` defines the URLs and allowed domains for Telegram, WhatsApp, MAX, and VK. `WebViewSessionManager` manages WebView2 controllers and a separate, stable profile for each `ServiceInstance`. Switching accounts must not delete a profile or require another sign-in. External links are handed to the system browser after navigation-policy checks.

WebView2 provides page titles and web-notification events. Notification coordinators account for the selected account, permissions, mute, Do Not Disturb, preview, and sound settings. Notification support depends on the site and the installed WebView2 Runtime.

## Mail

`Services/Mail` contains shared contracts and models for message lists, detail views, compose, attachments, search, and notifications.

- Gmail uses the Gmail API and Desktop OAuth in the system browser. Access may require renewed Google consent; the refresh credential is protected with DPAPI `CurrentUser`.
- Yandex Mail and Mail.ru use the managed IMAP/SMTP path. It uses UID/UIDVALIDITY for message identity, server-side search, safe pagination, mailbox actions, and server drafts. Capabilities and system folders are discovered from server responses; unsupported actions are not presented as working features.
- Other IMAP/SMTP accounts use user-provided connection settings. Available actions depend on each server's capabilities.

Mail credentials are held separately from JSON settings in DPAPI-protected storage. A protected local recovery mechanism handles unsaved IMAP drafts when shutdown cannot save them. The application processes ordinary messages for reading and sending; the privacy boundary is described in [security.md](security.md).

## Notifications and updates

Web messengers use WebView2 events; Gmail and IMAP providers use separate new-mail detection mechanisms. They share popup presentation and sound/Do Not Disturb settings. Local moves into the Inbox must not be treated as new incoming mail.

The update check requests the latest stable GitHub Release from `scripchenko/raven` and informs the user when a newer version is available. The app does not automatically download or install an update. The already-published `v0.1.0` binary retains the earlier `scripchenko/Lantern` URL; current source code uses the renamed repository.
