# Windows distribution

The raven v0.1.0 installer is available in [GitHub Releases](https://github.com/scripchenko/raven/releases/tag/v0.1.0) as `raven-Setup-0.1.0-win-x64.exe`. Inno Setup 6 builds it from a multi-file, self-contained .NET 10 `win-x64` publish. The internal executable name is `UnifiedMessenger.App.exe`.

## User requirements

- Windows 10 version 1809 or newer, or Windows 11, on x64 hardware;
- Microsoft Edge WebView2 Evergreen Runtime.

Neither the .NET SDK nor a separate .NET runtime is required to install raven. WebView2 Evergreen is not bundled: if the Runtime is missing, installation stops and offers to open the [official Microsoft download page](https://developer.microsoft.com/microsoft-edge/webview2/).

## Building from source

The build machine needs the .NET 10 SDK and, for the installer, Inno Setup 6. Run from the repository root:

```powershell
./scripts/build-windows-package.ps1
```

Pass a non-default .NET CLI path with `-DotNetPath`. The script restores dependencies, publishes with the `win-x64-self-contained` profile, verifies the package contents, and runs Inno Setup. For publish only, use `./scripts/publish-win-x64.ps1`; to verify an existing payload, use `./scripts/verify-windows-package.ps1`.

```text
artifacts\publish\win-x64\
artifacts\installer\raven-Setup-0.1.0-win-x64.exe
```

Git ignores `artifacts`. The publish does not use trimming, single-file, ReadyToRun, or Native AOT; PDB files are excluded from the user payload.

## Installation, shortcuts, and updates

The installer keeps the Inno `AppId` `{DFAA0CC1-B19F-4506-8124-750955F1C946}` and compatible physical installation directory `%LOCALAPPDATA%\Programs\Lantern`. It creates `raven` Start Menu and Desktop shortcuts with the Raven crow icon. The running window, taskbar, Alt+Tab, and tray use the older blue bracket icon; this is an intentional shell-identity compatibility choice for `Scripchenko.Raven`.

During an upgrade, Windows Restart Manager may offer to close a running raven instance before files are replaced. Installation does not automatically restart the app. The app checks for newer stable releases but does not download or install them automatically; the user runs the new installer.

Install, upgrade, and uninstall **do not migrate or delete** these user-data directories:

```text
%APPDATA%\UnifiedMessenger\
%LOCALAPPDATA%\UnifiedMessenger\
```

They retain settings, WebView2 profiles, protected mail credentials, and other local data. They are not part of the installer payload.

## Gmail OAuth

Gmail requires a local Google Desktop OAuth client JSON at `%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`. The file is neither included in the publish or installer nor tracked by Git. This user-supplied local configuration is **not a product-ready centralized OAuth client distribution**. See the [Gmail setup guide](gmail-setup.md).

## Signing

The v0.1.0 installer and executable are unsigned. Windows SmartScreen may warn on first launch. Verify the download source; do not disable SmartScreen globally.
