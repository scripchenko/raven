# Windows distribution

Stage 8.1 формирует folder-based self-contained publish для `win-x64` и минимальный per-user installer Lantern на Inno Setup 6. Внутренние имена проекта и исполняемого файла остаются `UnifiedMessenger.App`.

## Требования к машине сборки

- Windows x64;
- .NET SDK версии из `global.json` или совместимой feature band;
- Inno Setup 6 — только для сборки installer.

Устанавливать Inno Setup автоматически сценарий не пытается. Если `ISCC.exe` отсутствует, self-contained publish всё равно создаётся и проверяется, после чего выводится понятное предупреждение.

## Сборка publish и installer

Из корня репозитория:

```powershell
./scripts/build-windows-package.ps1
```

При нестандартном расположении .NET CLI путь можно передать явно:

```powershell
./scripts/build-windows-package.ps1 -DotNetPath "D:\Tools\dotnet\dotnet.exe"
```

Сценарий выполняет restore, self-contained publish через профиль `win-x64-self-contained.pubxml`, проверку содержимого и затем запускает Inno Setup, если compiler доступен.

Результаты:

```text
artifacts\publish\win-x64\
artifacts\installer\Lantern-Setup-0.1.0-win-x64.exe
```

Каталог `artifacts` игнорируется Git. Publish является multi-file: single-file, trimming, ReadyToRun и Native AOT отключены. PDB не включаются в пользовательскую поставку.

Для создания только проверенного publish можно выполнить:

```powershell
./scripts/publish-win-x64.ps1
```

Повторная проверка существующего publish:

```powershell
./scripts/verify-windows-package.ps1
```

## Установка и обновление

Installer устанавливает Lantern для текущего пользователя в:

```text
%LOCALAPPDATA%\Programs\Lantern
```

Создаётся обязательный ярлык Lantern в Start Menu. Ярлык на Desktop является опциональным и по умолчанию выключен.

Будущая версия использует тот же постоянный Inno Setup `AppId` и обновляет файлы в том же install directory. Если Lantern запущен или скрыт в tray, стандартный Windows Restart Manager предлагает закрыть его перед заменой файлов. Принудительное завершение не используется, а автоматический перезапуск приложения после upgrade отключён.

Auto-update и background updater в Stage 8.1 отсутствуют: обновление выполняется запуском более нового installer.

## Сохранение пользовательских данных

Install, upgrade и uninstall не переносят и не удаляют:

```text
%APPDATA%\UnifiedMessenger\
%LOCALAPPDATA%\UnifiedMessenger\
```

Здесь остаются настройки, WebView2 profiles, cookies, mail credentials под DPAPI CurrentUser, Google OAuth development configuration и privacy preferences. Эти каталоги не являются installer payload и не читаются packaging-сценариями.

Uninstall удаляет только install directory, shortcuts и запись Lantern в Installed Apps. Отдельной опции удаления пользовательских данных на этом этапе нет.

## Microsoft Edge WebView2 Runtime

Self-contained .NET publish не включает Microsoft Edge WebView2 Runtime. Installer проверяет официальный Evergreen registry key для per-user и per-machine установок.

Если Runtime отсутствует, установка Lantern останавливается и предлагает открыть официальную страницу Microsoft:

<https://developer.microsoft.com/microsoft-edge/webview2/>

WebView2 bootstrapper или standalone installer в Git и в payload Stage 8.1 не включается. На машине пользователя должен быть установлен Evergreen Runtime; существующая проверка внутри Lantern остаётся дополнительным fallback.

## Gmail OAuth

Текущая development Gmail OAuth configuration использует локальный файл:

```text
%LOCALAPPDATA%\UnifiedMessenger\GoogleOAuth\client_secret.json
```

Этот файл не включается в publish или installer, не отслеживается Git и не является product-ready provisioning для чистой внешней установки. Stage 8.1 не изменяет Gmail OAuth architecture.

## Подпись и SmartScreen

Stage 8.1 не создаёт сертификаты и не подписывает installer или EXE. При внешней загрузке Windows SmartScreen может показать предупреждение для неподписанного файла или файла без накопленной репутации. Code signing должен рассматриваться отдельным этапом перед публичным распространением.
