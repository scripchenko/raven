[CmdletBinding()]
param(
    [string]$DotNetPath = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'build-windows-package.ps1') `
    -DotNetPath $DotNetPath `
    -SkipInstaller
