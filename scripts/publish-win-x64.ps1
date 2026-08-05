$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try {
    dotnet publish `
        src/UnifiedMessenger.App/UnifiedMessenger.App.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false
}
finally {
    Pop-Location
}
