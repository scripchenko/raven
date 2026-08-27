using System.Resources;
using System.Windows.Resources;
using System.Xml.Linq;
using UnifiedMessenger.App.Services.Branding;

namespace UnifiedMessenger.Tests;

public sealed class Stage8PackagingTests
{
    private const string InstallerAppId = "DFAA0CC1-B19F-4506-8124-750955F1C946";
    private const string ShellAppUserModelId = "Scripchenko.Lantern";

    [Fact]
    public void ProjectMetadata_UsesLanternPreviewIdentityWithoutInternalRename()
    {
        XDocument project = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "UnifiedMessenger.App.csproj"));

        Assert.Equal("0.1.0", GetProperty(project, "Version"));
        Assert.Equal("0.1.0.0", GetProperty(project, "AssemblyVersion"));
        Assert.Equal("0.1.0.0", GetProperty(project, "FileVersion"));
        Assert.Equal("Lantern", GetProperty(project, "Product"));
        Assert.Equal("Lantern", GetProperty(project, "Title"));
        Assert.Equal("Lantern", GetProperty(project, "AssemblyTitle"));
        Assert.Equal("Windows-приложение для мессенджеров и почты.", GetProperty(project, "Description"));
        Assert.Equal("false", GetProperty(project, "GenerateAssemblyCompanyAttribute"));
        Assert.Equal("UnifiedMessenger.App", GetProperty(project, "AssemblyName"));
        Assert.Equal("UnifiedMessenger.App", GetProperty(project, "RootNamespace"));
    }

    [Fact]
    public void PublishProfile_IsSelfContainedWinX64WithoutAggressivePublishModes()
    {
        XDocument profile = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Properties", "PublishProfiles",
            "win-x64-self-contained.pubxml"));

        Assert.Equal("Release", GetProperty(profile, "Configuration"));
        Assert.Equal("win-x64", GetProperty(profile, "RuntimeIdentifier"));
        Assert.Equal("true", GetProperty(profile, "SelfContained"));
        Assert.Equal("true", GetProperty(profile, "UseAppHost"));
        Assert.Equal("false", GetProperty(profile, "PublishSingleFile"));
        Assert.Equal("false", GetProperty(profile, "PublishTrimmed"));
        Assert.Equal("false", GetProperty(profile, "PublishReadyToRun"));
        Assert.Equal("false", GetProperty(profile, "PublishAot"));
    }

    [Fact]
    public void PublishProfile_UsesDedicatedIgnoredArtifactsAndExcludesPdb()
    {
        XDocument profile = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Properties", "PublishProfiles",
            "win-x64-self-contained.pubxml"));

        Assert.EndsWith(
            "artifacts\\publish\\win-x64\\",
            GetProperty(profile, "PublishDir"),
            StringComparison.Ordinal);
        Assert.Equal("false", GetProperty(profile, "DebugSymbols"));
        Assert.Equal("None", GetProperty(profile, "DebugType"));

        string gitIgnore = File.ReadAllText(FindRepositoryFile(".gitignore"));
        Assert.Contains("**/artifacts/", gitIgnore, StringComparison.Ordinal);
    }

    [Fact]
    public void LanternSystemIcon_IsEmbeddedRuntimeResourceAndNotContent()
    {
        System.Reflection.Assembly assembly = typeof(BrandIconResources).Assembly;
        Assert.DoesNotContain(
            assembly.GetCustomAttributesData(),
            attribute =>
                attribute.AttributeType == typeof(AssemblyAssociatedContentFileAttribute)
                && string.Equals(
                    attribute.ConstructorArguments[0].Value as string,
                    "assets/branding/lantern_system.ico",
                    StringComparison.OrdinalIgnoreCase));

        using Stream resources = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream("UnifiedMessenger.App.g.resources"));
        using ResourceReader reader = new(resources);
        reader.GetResourceData(
            "assets/branding/lantern_system.ico",
            out string resourceType,
            out byte[] resourceBytes);

        Assert.False(string.IsNullOrWhiteSpace(resourceType));
        Assert.NotEmpty(resourceBytes);
    }

    [Fact]
    public void LanternApplicationIcon_IsPublishedForShellWithoutWpfResourceClassification()
    {
        XDocument project = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "UnifiedMessenger.App.csproj"));
        const string iconPath = "Assets\\Branding\\lantern.ico";

        Assert.DoesNotContain(project.Descendants("Resource"), item =>
            string.Equals((string?)item.Attribute("Include"), iconPath, StringComparison.Ordinal));
        Assert.DoesNotContain(project.Descendants("Content"), item =>
            string.Equals((string?)item.Attribute("Include"), iconPath, StringComparison.Ordinal));

        XElement publishItem = Assert.Single(project.Descendants("ResolvedFileToPublish"), item =>
            string.Equals(
                (string?)item.Attribute("Include"),
                "$(MSBuildProjectDirectory)\\Assets\\Branding\\lantern.ico",
                StringComparison.Ordinal));
        Assert.Equal(iconPath, publishItem.Element("RelativePath")?.Value);
        Assert.Equal("PreserveNewest", publishItem.Element("CopyToPublishDirectory")?.Value);
    }

    [Fact]
    public void LanternSystemIcon_IsPublishedSeparatelyWithoutContentClassification()
    {
        XDocument project = XDocument.Load(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "UnifiedMessenger.App.csproj"));
        const string iconPath = "Assets\\Branding\\lantern_system.ico";

        Assert.Single(project.Descendants("Resource"), item =>
            string.Equals((string?)item.Attribute("Include"), iconPath, StringComparison.Ordinal));
        Assert.DoesNotContain(project.Descendants("Content"), item =>
            string.Equals((string?)item.Attribute("Include"), iconPath, StringComparison.Ordinal));

        XElement publishItem = Assert.Single(project.Descendants("ResolvedFileToPublish"), item =>
            string.Equals(
                (string?)item.Attribute("Include"),
                "$(MSBuildProjectDirectory)\\Assets\\Branding\\lantern_system.ico",
                StringComparison.Ordinal));
        Assert.Equal(iconPath, publishItem.Element("RelativePath")?.Value);
        Assert.Equal("PreserveNewest", publishItem.Element("CopyToPublishDirectory")?.Value);
    }

    [Fact]
    public void Installer_UsesStablePerUserIdentityAndExpectedInstallDirectory()
    {
        string installer = ReadInstallerScript();

        Assert.Contains($"AppId={{{{{InstallerAppId}}}", installer, StringComparison.Ordinal);
        Assert.Contains("AppName={#AppName}", installer, StringComparison.Ordinal);
        Assert.Contains("AppVersion={#AppVersion}", installer, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\Lantern", installer, StringComparison.Ordinal);
        Assert.Contains("ArchitecturesAllowed=x64compatible", installer, StringComparison.Ordinal);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("AppPublisher=", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installer_CreatesStartMenuAndUncheckedDesktopShortcut()
    {
        string installer = ReadInstallerScript();

        Assert.Contains(
            "Name: \"{autoprograms}\\Lantern\"; Filename: \"{app}\\{#AppExeName}\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Name: \"desktopicon\"; Description: \"{cm:CreateDesktopIcon}\";",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("Flags: unchecked", installer, StringComparison.Ordinal);
        Assert.Contains(
            "Name: \"{autodesktop}\\Lantern\"; Filename: \"{app}\\{#AppExeName}\";",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("Tasks: desktopicon", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_StartMenuUsesCompactIcon_WhileDesktopExeAndSetupKeepFullIcon()
    {
        string installer = ReadInstallerScript();
        string project = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "UnifiedMessenger.App.csproj"));
        string mainWindow = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MainWindow.xaml"));

        Assert.Contains(
            "Name: \"{autoprograms}\\Lantern\"; Filename: \"{app}\\{#AppExeName}\"; WorkingDir: \"{app}\"; IconFilename: \"{app}\\Assets\\Branding\\lantern_system.ico\"; AppUserModelID: \"{#AppUserModelId}\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Name: \"{autodesktop}\\Lantern\"; Filename: \"{app}\\{#AppExeName}\"; WorkingDir: \"{app}\"; IconFilename: \"{app}\\Assets\\Branding\\lantern.ico\"; AppUserModelID: \"{#AppUserModelId}\"; Tasks: desktopicon",
            installer,
            StringComparison.Ordinal);
        Assert.Contains("SetupIconFile={#PublishDir}\\Assets\\Branding\\lantern.ico", installer, StringComparison.Ordinal);
        Assert.Contains("<ApplicationIcon>Assets\\Branding\\lantern.ico</ApplicationIcon>", project, StringComparison.Ordinal);
        Assert.Contains(
            "Icon=\"/UnifiedMessenger.App;component/Assets/Branding/lantern_system.ico\"",
            mainWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShellIdentity_UsesStableAppIdAndPhysicalCompactRelaunchIcon()
    {
        Assert.Equal(ShellAppUserModelId, WindowsShellIdentity.ApplicationUserModelId);
        Assert.Equal(
            Path.Combine("C:\\Lantern", "Assets", "Branding", "lantern_system.ico") + ",0",
            WindowsShellIdentity.CreateRelaunchIconResource("C:\\Lantern"));
        Assert.Equal(
            "\"C:\\Lantern\\UnifiedMessenger.App.exe\"",
            WindowsShellIdentity.CreateRelaunchCommand("C:\\Lantern\\UnifiedMessenger.App.exe"));

        string installer = ReadInstallerScript();
        Assert.Contains($"#define AppUserModelId \"{ShellAppUserModelId}\"", installer, StringComparison.Ordinal);
        Assert.Equal(
            2,
            installer.Split($"AppUserModelID: \"{{#AppUserModelId}}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains($"AppId={{{{{InstallerAppId}}}", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellIdentity_IsAppliedBeforeUiAndWindowTaskbarRegistration()
    {
        string app = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "App.xaml.cs"));
        string mainWindow = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Views", "MainWindow.xaml.cs"));
        string taskbarIndicator = File.ReadAllText(FindRepositoryFile(
            "src", "UnifiedMessenger.App", "Services", "Tray", "WpfTaskbarActivityIndicator.cs"));

        Assert.True(
            app.IndexOf("WindowsShellIdentity.TryInitializeProcess()", StringComparison.Ordinal)
            < app.IndexOf("base.OnStartup(e)", StringComparison.Ordinal));
        Assert.Contains(
            "WindowsShellIdentity.TryApplyToWindow(_mainWindowHandle)",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains("taskbarItem.Overlay = HasActivity ? ActivityOverlay : null", taskbarIndicator, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_PayloadComesOnlyFromPublishDirectory()
    {
        string installer = ReadInstallerScript();
        string[] sourceEntries = installer
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Single(sourceEntries);
        Assert.Equal(
            "Source: \"{#PublishDir}\\*\"; DestDir: \"{app}\"; Flags: ignoreversion recursesubdirs createallsubdirs",
            sourceEntries[0].Trim());
        Assert.DoesNotContain("..\\bin", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..\\obj", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..\\.git", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uninstaller_DoesNotDeleteUnifiedMessengerUserData()
    {
        string installer = ReadInstallerScript();

        Assert.DoesNotContain("[UninstallDelete]", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DelTree(", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{localappdata}\\UnifiedMessenger", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{userappdata}\\UnifiedMessenger", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Installer_UsesSafeRestartManagerAndOfficialWebView2Detection()
    {
        string installer = ReadInstallerScript();

        Assert.Contains("CloseApplications=yes", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseApplications=force", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RestartApplications=no", installer, StringComparison.Ordinal);
        Assert.Contains("F3017226-FE2A-4295-8BDF-00C3A9A7E4C5", installer, StringComparison.Ordinal);
        Assert.Contains("Microsoft\\EdgeUpdate\\Clients", installer, StringComparison.Ordinal);
        Assert.Contains("developer.microsoft.com/microsoft-edge/webview2/", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("MicrosoftEdgeWebView2Setup.exe", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagingScripts_CheckSensitiveNamesWithoutUserSpecificPaths()
    {
        string buildScript = File.ReadAllText(FindRepositoryFile("scripts", "build-windows-package.ps1"));
        string verificationScript = File.ReadAllText(FindRepositoryFile("scripts", "verify-windows-package.ps1"));
        string combined = buildScript + verificationScript;

        Assert.DoesNotContain("C:\\Users\\", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client_secret*.json", verificationScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("settings.json", verificationScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.credential.bin", verificationScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'WebView2'", verificationScript, StringComparison.Ordinal);
        Assert.Contains("'.git'", verificationScript, StringComparison.Ordinal);
    }

    [Fact]
    public void DistributionDocumentation_DeclaresDataPreservationAndGmailLimitation()
    {
        string documentation = File.ReadAllText(FindRepositoryFile("docs", "windows-distribution.md"));

        Assert.Contains("%APPDATA%\\UnifiedMessenger", documentation, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\UnifiedMessenger", documentation, StringComparison.Ordinal);
        Assert.Contains("не переносят и не удаляют", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client_secret.json", documentation, StringComparison.Ordinal);
        Assert.Contains("не является product-ready", documentation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SmartScreen", documentation, StringComparison.Ordinal);
    }

    private static string ReadInstallerScript() =>
        File.ReadAllText(FindRepositoryFile("installer", "Lantern.iss"));

    private static string GetProperty(XDocument document, string propertyName) =>
        document.Descendants(propertyName).Single().Value.Trim();

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
