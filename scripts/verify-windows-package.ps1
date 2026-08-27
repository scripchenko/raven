[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$PublishDirectory,
    [string]$InstallerScriptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $RepositoryRoot 'artifacts\publish\win-x64'
}

if ([string]::IsNullOrWhiteSpace($InstallerScriptPath)) {
    $InstallerScriptPath = Join-Path $RepositoryRoot 'installer\Lantern.iss'
}

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$InstallerScriptPath = [IO.Path]::GetFullPath($InstallerScriptPath)
$expectedPublishRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts\publish'))

function Assert-PathWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Candidate,
        [Parameter(Mandatory)]
        [string]$Parent,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    if (-not $candidateFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label находится вне разрешённого каталога: $candidateFull"
    }
}

function Assert-FileExists {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label не найден: $Path"
    }
}

function Assert-PublishedLanternResource {
    param(
        [Parameter(Mandatory)]
        [string]$AssemblyPath
    )

    $hostExecutable = (Get-Process -Id $PID).Path
    $probeVariableName = 'LANTERN_PUBLISH_RESOURCE_PROBE_ASSEMBLY'
    $previousProbeValue = [Environment]::GetEnvironmentVariable($probeVariableName, 'Process')
    [Environment]::SetEnvironmentVariable($probeVariableName, $AssemblyPath, 'Process')

    $probe = @'
$ErrorActionPreference = 'Stop'
$assemblyPath = [Environment]::GetEnvironmentVariable(
    'LANTERN_PUBLISH_RESOURCE_PROBE_ASSEMBLY',
    'Process')
$assembly = [Reflection.Assembly]::LoadFile($assemblyPath)
$contentIcons = @($assembly.GetCustomAttributesData() | Where-Object {
    $_.AttributeType.FullName -eq 'System.Windows.Resources.AssemblyAssociatedContentFileAttribute' -and
    [string]$_.ConstructorArguments[0].Value -in @(
        'assets/branding/lantern.ico',
        'assets/branding/lantern_system.ico')
})
if ($contentIcons.Count -ne 0) {
    throw 'Lantern icons ошибочно классифицированы как WPF Content.'
}

$resourceStream = $assembly.GetManifestResourceStream('UnifiedMessenger.App.g.resources')
if ($null -eq $resourceStream) {
    throw 'UnifiedMessenger.App.g.resources отсутствует.'
}

try {
    $reader = [Resources.ResourceReader]::new($resourceStream)
    try {
        $requiredIcons = @('assets/branding/lantern_system.ico')
        $foundIcons = @{}
        $entries = $reader.GetEnumerator()
        while ($entries.MoveNext()) {
            $key = [string]$entries.Key
            if ($requiredIcons -contains $key) {
                $foundIcons[$key] = $true
            }
        }

        foreach ($requiredIcon in $requiredIcons) {
            if (-not $foundIcons.ContainsKey($requiredIcon)) {
                throw "Embedded Lantern icon отсутствует в WPF resources: $requiredIcon"
            }
        }
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $resourceStream.Dispose()
}

Add-Type -AssemblyName PresentationFramework
$application = [System.Windows.Application]::new()
try {
    $resourceType = $assembly.GetType(
        'UnifiedMessenger.App.Services.Branding.BrandIconResources',
        $true)
    $icon = $null
    $icon = $resourceType.GetMethod('LoadSystemIcon').Invoke($null, @())
    try {
        if ($null -eq $icon -or $icon.Width -le 0 -or $icon.Height -le 0) {
            throw 'Production Lantern icon loader вернул некорректный icon.'
        }
    }
    finally {
        if ($null -ne $icon) {
            $icon.Dispose()
        }
    }
}
finally {
    $application.Shutdown()
}
'@

    try {
        & $hostExecutable -NoLogo -NoProfile -NonInteractive -Command $probe
        if ($LASTEXITCODE -ne 0) {
            throw "Runtime-проверка embedded Lantern icon завершилась с кодом $LASTEXITCODE."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $probeVariableName,
            $previousProbeValue,
            'Process')
    }
}

Assert-PathWithin -Candidate $PublishDirectory -Parent $expectedPublishRoot -Label 'Publish output'
Assert-PathWithin -Candidate $InstallerScriptPath -Parent $RepositoryRoot -Label 'Installer script'

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish output не найден: $PublishDirectory"
}

Assert-FileExists -Path (Join-Path $PublishDirectory 'UnifiedMessenger.App.exe') -Label 'Основной EXE'
Assert-FileExists -Path (Join-Path $PublishDirectory 'UnifiedMessenger.App.dll') -Label 'Основная сборка'
Assert-FileExists -Path (Join-Path $PublishDirectory 'UnifiedMessenger.App.deps.json') -Label 'Dependency manifest'
Assert-FileExists -Path (Join-Path $PublishDirectory 'UnifiedMessenger.App.runtimeconfig.json') -Label 'Runtime configuration'
Assert-FileExists -Path (Join-Path $PublishDirectory 'hostfxr.dll') -Label 'Self-contained hostfxr'
Assert-FileExists -Path (Join-Path $PublishDirectory 'hostpolicy.dll') -Label 'Self-contained hostpolicy'
Assert-FileExists -Path (Join-Path $PublishDirectory 'coreclr.dll') -Label 'Self-contained CoreCLR'
Assert-FileExists -Path (Join-Path $PublishDirectory 'PresentationFramework.dll') -Label 'WPF runtime'
$publishedLanternIconPath = Join-Path $PublishDirectory 'Assets\Branding\lantern.ico'
$sourceLanternIconPath = Join-Path $RepositoryRoot 'src\UnifiedMessenger.App\Assets\Branding\lantern.ico'
$publishedLanternSystemIconPath = Join-Path $PublishDirectory 'Assets\Branding\lantern_system.ico'
$sourceLanternSystemIconPath = Join-Path $RepositoryRoot 'src\UnifiedMessenger.App\Assets\Branding\lantern_system.ico'
Assert-FileExists -Path $publishedLanternIconPath -Label 'Published Lantern icon'
Assert-FileExists -Path $sourceLanternIconPath -Label 'Source Lantern icon'
Assert-FileExists -Path $publishedLanternSystemIconPath -Label 'Published Lantern system icon'
Assert-FileExists -Path $sourceLanternSystemIconPath -Label 'Source Lantern system icon'
Assert-FileExists -Path $InstallerScriptPath -Label 'Installer script'

if ((Get-FileHash -LiteralPath $publishedLanternIconPath -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $sourceLanternIconPath -Algorithm SHA256).Hash) {
    throw 'Published Lantern icon не совпадает с исходным branding resource.'
}

if ((Get-FileHash -LiteralPath $publishedLanternSystemIconPath -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $sourceLanternSystemIconPath -Algorithm SHA256).Hash) {
    throw 'Published Lantern system icon не совпадает с исходным compact resource.'
}

Assert-PublishedLanternResource -AssemblyPath (Join-Path $PublishDirectory 'UnifiedMessenger.App.dll')

$webViewLoaders = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File |
    Where-Object { $_.Name -eq 'WebView2Loader.dll' })
$webViewLoaderPaths = @($webViewLoaders | ForEach-Object {
    $_.FullName.Substring($PublishDirectory.Length).TrimStart('\', '/')
} | Sort-Object)
$expectedWebViewLoaderPaths = @(
    'runtimes\win-x64\native\WebView2Loader.dll',
    'WebView2Loader.dll'
) | Sort-Object

if ($webViewLoaderPaths.Count -ne $expectedWebViewLoaderPaths.Count -or
    (Compare-Object -ReferenceObject $expectedWebViewLoaderPaths -DifferenceObject $webViewLoaderPaths)) {
    throw "Набор WebView2Loader.dll не соответствует ожидаемому win-x64 publish: $($webViewLoaderPaths -join ', ')"
}

$webViewLoaderHashes = @($webViewLoaders | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
} | Select-Object -Unique)
if ($webViewLoaderHashes.Count -ne 1) {
    throw 'WebView2Loader.dll в корне и RID-каталоге не идентичны.'
}

foreach ($loader in $webViewLoaders) {
    $bytes = [IO.File]::ReadAllBytes($loader.FullName)
    $peOffset = [BitConverter]::ToInt32($bytes, 60)
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x8664) {
        throw "WebView2Loader.dll не является x64 PE: $($loader.FullName)"
    }
}

$runtimeConfigPath = Join-Path $PublishDirectory 'UnifiedMessenger.App.runtimeconfig.json'
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$frameworksProperty = $runtimeConfig.runtimeOptions.PSObject.Properties['frameworks']
if ($null -ne $frameworksProperty -and $null -ne $frameworksProperty.Value) {
    throw 'runtimeconfig содержит framework-dependent frameworks; ожидался self-contained publish.'
}

$includedFrameworksProperty = $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']
if ($null -eq $includedFrameworksProperty) {
    throw 'runtimeconfig не содержит self-contained includedFrameworks.'
}

$includedFrameworkNames = @($includedFrameworksProperty.Value | ForEach-Object { $_.name })
if ($includedFrameworkNames -notcontains 'Microsoft.NETCore.App' -or
    $includedFrameworkNames -notcontains 'Microsoft.WindowsDesktop.App') {
    throw 'runtimeconfig не подтверждает включённые .NET и Windows Desktop frameworks.'
}

$depsPath = Join-Path $PublishDirectory 'UnifiedMessenger.App.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($deps.runtimeTarget.name -notmatch '/win-x64$') {
    throw "Dependency manifest не соответствует win-x64: $($deps.runtimeTarget.name)"
}

$files = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File)
if ($files.Count -eq 0) {
    throw 'Publish output пуст.'
}

$forbiddenDirectoryNames = @(
    '.git', '.vs', 'Credentials', 'Diagnostics', 'GoogleOAuth', 'Logs',
    'TestResults', 'User Data', 'WebView2', 'screenshots', 'tests'
)
$allowedExtensions = @('.config', '.dll', '.exe', '.ico', '.json')

foreach ($file in $files) {
    $relativePath = $file.FullName.Substring($PublishDirectory.Length).TrimStart('\', '/')
    $segments = @($relativePath -split '[\\/]')
    $directorySegments = if ($segments.Count -gt 1) { @($segments[0..($segments.Count - 2)]) } else { @() }

    foreach ($segment in $directorySegments) {
        if ($forbiddenDirectoryNames -contains $segment) {
            throw "Запрещённый каталог в publish output: $relativePath"
        }
    }

    $name = $file.Name
    $lowerName = $name.ToLowerInvariant()
    $forbiddenName =
        $lowerName -eq 'settings.json' -or
        $lowerName -eq 'settings.local.json' -or
        $lowerName -eq 'credentials.json' -or
        $lowerName -eq 'secrets.json' -or
        $lowerName -eq 'cookies' -or
        $lowerName -like 'cookies-*' -or
        $lowerName -like 'client_secret*.json' -or
        $lowerName -like '*.credential.bin' -or
        $lowerName -like '*.trust.bin' -or
        $lowerName -like '*.token' -or
        $lowerName -like '*.tokens' -or
        $lowerName -like '*.log' -or
        $lowerName -like '*.pdb' -or
        $lowerName -like '*.pfx' -or
        $lowerName -like '*.p12' -or
        $lowerName -like '*.pem' -or
        $lowerName -like '*.key' -or
        $lowerName -like '*.snk'

    if ($forbiddenName) {
        throw "Запрещённый файл в publish output: $relativePath"
    }

    if ($allowedExtensions -notcontains $file.Extension.ToLowerInvariant()) {
        throw "Неожиданный тип файла в publish output: $relativePath"
    }
}

$textFiles = $files | Where-Object { $_.Extension -in @('.config', '.json') }
foreach ($textFile in $textFiles) {
    $matches = Select-String -LiteralPath $textFile.FullName -Pattern '[A-Za-z]:\\Users\\' -AllMatches
    if ($matches) {
        throw "В publish metadata найден абсолютный пользовательский путь: $($textFile.Name)"
    }
}

$installerScript = Get-Content -LiteralPath $InstallerScriptPath -Raw -Encoding UTF8
$fileSources = [regex]::Matches($installerScript, '(?im)^\s*Source\s*:\s*"([^"]+)"')
if ($fileSources.Count -ne 1 -or $fileSources[0].Groups[1].Value -ne '{#PublishDir}\*') {
    throw 'Installer должен иметь единственный payload Source из {#PublishDir}\*.'
}

$startMenuShortcutIconFragment = 'Name: "{autoprograms}\Lantern"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Branding\lantern_system.ico"; AppUserModelID: "{#AppUserModelId}"'
if (-not $installerScript.Contains($startMenuShortcutIconFragment, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Start Menu shortcut должен использовать compact Lantern system icon.'
}

$desktopShortcutIconFragment = 'Name: "{autodesktop}\Lantern"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\Branding\lantern.ico"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon'
if (-not $installerScript.Contains($desktopShortcutIconFragment, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Desktop shortcut должен использовать full Lantern icon.'
}

if (-not $installerScript.Contains('#define AppUserModelId "Scripchenko.Lantern"', [StringComparison]::Ordinal)) {
    throw 'Installer shortcuts должны использовать stable Lantern AppUserModelID.'
}

$forbiddenInstallerFragments = @(
    '..\bin', '..\obj', '..\.git',
    '{localappdata}\UnifiedMessenger',
    '{userappdata}\UnifiedMessenger',
    '[UninstallDelete]',
    'DelTree('
)
foreach ($fragment in $forbiddenInstallerFragments) {
    if ($installerScript.Contains($fragment, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer script содержит запрещённый fragment: $fragment"
    }
}

$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host 'Windows package verification: PASS'
Write-Host "Publish directory: $PublishDirectory"
Write-Host "Files: $($files.Count)"
Write-Host ('Size: {0:N2} MiB' -f ($totalBytes / 1MB))
Write-Host 'PDB: excluded from distribution publish'
Write-Host 'Sensitive/user-data filenames: absent'
Write-Host 'Installer payload source: publish directory only'
