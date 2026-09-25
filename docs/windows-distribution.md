# Выпуск для Windows

Пользовательский выпуск raven v0.1.0 доступен в [GitHub Releases](https://github.com/scripchenko/raven/releases/tag/v0.1.0) как `raven-Setup-0.1.0-win-x64.exe`. Installer создан Inno Setup 6 из многофайловой автономной публикации .NET 10 для `win-x64`. Внутреннее имя EXE — `UnifiedMessenger.App.exe`.

## Требования для пользователя

- Windows 10 версии 1809 или новее либо Windows 11, x64;
- Microsoft Edge WebView2 Evergreen Runtime.

.NET SDK и отдельный .NET runtime для установки не требуются. WebView2 Evergreen не входит в installer: если Runtime отсутствует, установка останавливается и предлагает открыть [официальную страницу Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/).

## Сборка из исходников

На машине разработчика нужны .NET 10 SDK и, для installer, Inno Setup 6. Из корня репозитория:

```powershell
./scripts/build-windows-package.ps1
```

Нестандартный путь к .NET CLI передаётся параметром `-DotNetPath`. Сценарий восстанавливает зависимости, публикует через профиль `win-x64-self-contained`, проверяет содержимое и запускает Inno Setup. Для одной только публикации есть `./scripts/publish-win-x64.ps1`; для проверки готового payload — `./scripts/verify-windows-package.ps1`.

```text
artifacts\publish\win-x64\
artifacts\installer\raven-Setup-0.1.0-win-x64.exe
```

`artifacts` игнорируется Git. Публикация не использует trimming, single-file, ReadyToRun и Native AOT; PDB исключены из пользовательского payload.

## Установка, ярлыки и обновления

Installer использует постоянный Inno `AppId` `{DFAA0CC1-B19F-4506-8124-750955F1C946}` и совместимый физический каталог `%LOCALAPPDATA%\Programs\Lantern`. Он создаёт ярлыки `raven` в Start Menu и на Desktop с Raven crow. Запущенное окно, taskbar, Alt+Tab и трей используют старую синюю скобку; это сознательная совместимая настройка shell identity `Scripchenko.Raven`.

При upgrade Windows Restart Manager может предложить закрыть работающий raven перед заменой файлов. Автоматического перезапуска после установки нет. Приложение проверяет наличие новых стабильных выпусков, но не скачивает и не устанавливает их автоматически: пользователь запускает installer новой версии сам.

Install, upgrade и uninstall **не переносят и не удаляют** пользовательские каталоги:

```text
%APPDATA%\UnifiedMessenger\
%LOCALAPPDATA%\UnifiedMessenger\
```

В них остаются настройки, WebView2-профили, защищённые почтовые credentials и другие локальные данные. Каталоги не входят в installer payload.

## Gmail OAuth

Для Gmail нужен локальный Google Desktop OAuth client JSON по пути `%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json`. Файл не включён в publish/installer и не отслеживается Git. Такая пользовательская локальная конфигурация **не является product-ready** централизованной выдачей OAuth-клиента; шаги описаны в [инструкции по Gmail](gmail-setup.md).

## Подпись

Installer и EXE v0.1.0 не подписаны цифровой подписью. При первом запуске Windows SmartScreen может показать предупреждение. Проверяйте источник загрузки; не отключайте SmartScreen глобально.
