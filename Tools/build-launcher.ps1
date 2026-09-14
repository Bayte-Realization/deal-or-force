param(
    [string]$OutputDirectory,
    [string]$DotNet = 'C:\Program Files\dotnet\dotnet.exe'
)
$ErrorActionPreference = 'Stop'
$launcherProjectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = $launcherProjectRoot }
$launcherPublish = Join-Path $launcherProjectRoot 'build\launcher-publish'
& $DotNet publish (Join-Path $PSScriptRoot 'Launcher\DealOrForce.Launcher.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $launcherPublish
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $launcherPublish 'Deal or Force Launcher.exe') -Destination (Join-Path $OutputDirectory 'Deal or Force Launcher.exe') -Force
if ([IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/') -ne [IO.Path]::GetFullPath($launcherProjectRoot).TrimEnd('\','/')) {
    Copy-Item -LiteralPath (Join-Path $launcherProjectRoot 'docs\LAUNCHER_RU.md') -Destination (Join-Path $OutputDirectory 'LAUNCHER_RU.md') -Force
}
Write-Host "Launcher ready: $OutputDirectory\Deal or Force Launcher.exe"
