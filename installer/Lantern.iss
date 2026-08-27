#define AppName "Lantern"
#define AppVersion "0.1.0"
#define AppExeName "UnifiedMessenger.App.exe"
#define AppUserModelId "Scripchenko.Lantern"
#define PublishDir "..\artifacts\publish\win-x64"
#define InstallerOutputDir "..\artifacts\installer"

[Setup]
AppId={{DFAA0CC1-B19F-4506-8124-750955F1C946}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppComments=Windows-приложение для мессенджеров и почты.
DefaultDirName={localappdata}\Programs\Lantern
DefaultGroupName=Lantern
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Lantern-Setup-{#AppVersion}-win-x64
SetupIconFile={#PublishDir}\Assets\Branding\lantern.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName=Lantern
VersionInfoDescription=Lantern Setup
VersionInfoProductName=Lantern
VersionInfoProductVersion=0.1.0
VersionInfoVersion=0.1.0.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
ChangesAssociations=no
ChangesEnvironment=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lantern"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Branding\lantern_system.ico"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\Lantern"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Branding\lantern.ico"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Code]
const
  WebView2ClientKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2DownloadUrl = 'https://developer.microsoft.com/microsoft-edge/webview2/';

function HasUsableWebView2Version(const Version: String): Boolean;
begin
  Result := (Trim(Version) <> '') and (CompareText(Trim(Version), '0.0.0.0') <> 0);
end;

function IsWebView2RuntimeInstalled: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKCU, WebView2ClientKey, 'pv', Version) and
      HasUsableWebView2Version(Version)) or
    (RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', Version) and
      HasUsableWebView2Version(Version)) or
    (RegQueryStringValue(HKLM64, WebView2ClientKey, 'pv', Version) and
      HasUsableWebView2Version(Version));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := IsWebView2RuntimeInstalled;
  if Result then
    Exit;

  if MsgBox(
    'Для работы Lantern требуется Microsoft Edge WebView2 Evergreen Runtime.' + #13#10 + #13#10 +
    'Установите Runtime с официальной страницы Microsoft, затем снова запустите установку Lantern.' + #13#10 + #13#10 +
    'Открыть официальную страницу сейчас?',
    mbConfirmation,
    MB_YESNO) = IDYES then
  begin
    if not ShellExec('open', WebView2DownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
      MsgBox('Не удалось открыть страницу Microsoft. Код ошибки: ' + IntToStr(ErrorCode), mbError, MB_OK);
  end;
end;
