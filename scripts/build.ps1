$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try {
    dotnet restore
    dotnet build -c Release
}
finally {
    Pop-Location
}
