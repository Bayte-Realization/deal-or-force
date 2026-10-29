param(
    [string]$OutputDirectory,
    [string]$DotNet = 'C:\Program Files\dotnet\dotnet.exe',
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$launcherProjectRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = $launcherProjectRoot }
$launcherPublish = if ($PublishDirectory) { [IO.Path]::GetFullPath($PublishDirectory) } else { Join-Path $launcherProjectRoot 'build\launcher-publish' }
& $DotNet publish (Join-Path $PSScriptRoot 'Launcher\DealOrForce.Launcher.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $launcherPublish
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
$launcherExpectedVersion = ([xml](Get-Content -LiteralPath (Join-Path $launcherProjectRoot 'GameVersion.props') -Raw)).Project.PropertyGroup.Version
$launcherPublishedExe = Get-Item -LiteralPath (Join-Path $launcherPublish 'Deal or Force Launcher.exe')
if ($launcherPublishedExe.VersionInfo.ProductVersion -ne $launcherExpectedVersion) {
    throw "Launcher version mismatch: expected $launcherExpectedVersion, got $($launcherPublishedExe.VersionInfo.ProductVersion). Not installing."
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $launcherPublish 'Deal or Force Launcher.exe') -Destination (Join-Path $OutputDirectory 'Deal or Force Launcher.exe') -Force

# docs\LAUNCHER_RU.md used to be copied in beside the executable. It is written for whoever
# builds the launcher, not for whoever plays the game, and it is Russian-only next to an
# English build - so a player opening the folder met a document in a language they may not
# read, about a thing that is not their problem. It stays in docs/, where it belongs.

Write-Host "Launcher ready: $OutputDirectory\Deal or Force Launcher.exe"
