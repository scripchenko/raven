[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet',
    [string]$InnoCompilerPath,
    [switch]$SkipInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'publish\win-x64'))
$installerOutputDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'installer'))
$projectPath = Join-Path $repositoryRoot 'src\UnifiedMessenger.App\UnifiedMessenger.App.csproj'
$installerScriptPath = Join-Path $repositoryRoot 'installer\Lantern.iss'
$verificationScriptPath = Join-Path $PSScriptRoot 'verify-windows-package.ps1'
$expectedInstallerPath = Join-Path $installerOutputDirectory 'raven-Setup-0.1.0-win-x64.exe'

function Assert-ArtifactPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    if (-not $fullPath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Отказ от очистки пути вне artifacts: $fullPath"
    }
}

function Reset-ArtifactDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Assert-ArtifactPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Resolve-Executable {
    param(
        [Parameter(Mandatory)]
        [string]$PathOrCommand,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if ([IO.Path]::IsPathRooted($PathOrCommand)) {
        if (-not (Test-Path -LiteralPath $PathOrCommand -PathType Leaf)) {
            throw "$Label не найден: $PathOrCommand"
        }

        return [IO.Path]::GetFullPath($PathOrCommand)
    }

    $command = Get-Command $PathOrCommand -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        throw "$Label не найден в PATH: $PathOrCommand"
    }

    return $command.Source
}

function Invoke-ExternalTool {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$Label
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label завершился с кодом $LASTEXITCODE."
    }
}

function Find-InnoCompiler {
    if (-not [string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
        return Resolve-Executable -PathOrCommand $InnoCompilerPath -Label 'Inno Setup compiler'
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates += Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
    }

    return $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

$dotnet = Resolve-Executable -PathOrCommand $DotNetPath -Label '.NET CLI'
Reset-ArtifactDirectory -Path $publishDirectory
if (-not $SkipInstaller) {
    Reset-ArtifactDirectory -Path $installerOutputDirectory
}

Push-Location $repositoryRoot
try {
    Invoke-ExternalTool -FilePath $dotnet -Arguments @(
        'restore',
        $projectPath,
        '--runtime', 'win-x64'
    ) -Label 'dotnet restore (win-x64)'
    Invoke-ExternalTool -FilePath $dotnet -Arguments @(
        'publish',
        $projectPath,
        '--configuration', 'Release',
        '--no-restore',
        '-p:PublishProfile=win-x64-self-contained'
    ) -Label 'dotnet publish'

    & $verificationScriptPath `
        -RepositoryRoot $repositoryRoot `
        -PublishDirectory $publishDirectory `
        -InstallerScriptPath $installerScriptPath

    if ($SkipInstaller) {
        Write-Host 'Installer build skipped by request.'
        return
    }

    $innoCompiler = Find-InnoCompiler
    if ([string]::IsNullOrWhiteSpace($innoCompiler)) {
        Write-Warning 'Inno Setup 6 compiler (ISCC.exe) не найден. Publish успешно создан и проверен; installer не собран. Установите Inno Setup 6 вручную и повторите сценарий.'
        return
    }

    Invoke-ExternalTool -FilePath $innoCompiler -Arguments @($installerScriptPath) -Label 'Inno Setup compiler'
    if (-not (Test-Path -LiteralPath $expectedInstallerPath -PathType Leaf)) {
        throw "Inno Setup завершился успешно, но installer не найден: $expectedInstallerPath"
    }

    Write-Host "Installer created: $expectedInstallerPath"
}
finally {
    Pop-Location
}
