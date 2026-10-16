param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [string]$SourceDirectory = (Join-Path $PSScriptRoot 'template'),
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$engine = (Resolve-Path -LiteralPath $GodotExecutable).Path
$version = (& $engine --version | Out-String).Trim()
if ($LASTEXITCODE -or $version -notmatch '^4\.8\.beta1\.mono\.') {
    throw "This tested template targets Godot 4.8 beta1 .NET, found: $version"
}
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $source) { throw 'Output must be a separate generated project.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
# Copy only our known template files. Never reset an existing project or overwrite edits.
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $destination = Join-Path $output $relative
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Existing generated source differs; preserved: $destination"
        }
        continue
    }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
& dotnet build (Join-Path $output 'LanguageKitFSharp.csproj') -c Debug
if ($LASTEXITCODE) { throw 'F# / C# bridge build failed.' }
Write-Host "F# managed bridge built for $version in $output"
