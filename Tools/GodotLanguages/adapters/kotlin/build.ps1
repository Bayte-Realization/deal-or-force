[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$JavaHome,
    [Parameter(Mandatory)][string]$VcVarsPath,
    [int]$JavaVersion = 25,
    [ValidateRange(1,16)][int]$Jobs = 4
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $source rev-parse HEAD) -ne '5d39c7e3fbe5be78853837e3c89676e7a92c9ec9') {
    throw 'Use the pinned godot-jvm 1.0.0-rc4 source revision.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $source -or $output.StartsWith($source + [IO.Path]::DirectorySeparatorChar)) {
    throw 'Output must be outside the upstream source directory.'
}
foreach ($path in @($source, $VcVarsPath)) {
    if ($path.Contains('"') -or $path.Contains("`n")) { throw 'Unsupported quote or newline in tool path.' }
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$archive = Join-Path $output 'godot-jvm-addon-1.0.0-rc4.zip'
$expected = '22424ffe160fec0fe901abafefefd969140fe2b0e4e1fd5ae44b0a172a6d28fe'
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://github.com/utopia-rise/godot-jvm/releases/download/1.0.0-rc4/godot-jvm-addon-1.0.0-rc4.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Godot-JVM addon archive checksum mismatch.'
}
$project = Join-Path $output 'project'
if (-not (Test-Path -LiteralPath $project)) {
    Expand-Archive -LiteralPath $archive -DestinationPath $project
}
Copy-Item -Path (Join-Path $PSScriptRoot 'template/*') -Destination $project -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $project '.godot') | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'template/extension_list.cfg.in') -Destination (Join-Path $project '.godot/extension_list.cfg') -Force
Copy-Item -LiteralPath (Join-Path $source 'kt/gradlew.bat') -Destination $project -Force
Copy-Item -LiteralPath (Join-Path $source 'kt/gradle') -Destination $project -Recurse -Force
$env:JAVA_HOME = (Resolve-Path -LiteralPath $JavaHome).Path
& (Join-Path $project 'gradlew.bat') -p $project build --console=plain "-PdofJavaVersion=$JavaVersion" *> (Join-Path $output 'gradle-build.log')
if ($LASTEXITCODE -ne 0) { throw "Kotlin build failed; see $output/gradle-build.log" }

# Keep a reproducible native source-build route. Cold GDExtension discovery in
# the tested beta editor can fail at shutdown, so the fresh probe's known
# extension list is supplied before launch; a successful calculation alone is
# never accepted without a clean editor import and process exit.
& git -C $source submodule update --init --depth 1 godot-cpp
if ($LASTEXITCODE -ne 0) { throw 'Could not initialize pinned godot-cpp.' }
$nativeCommand = 'call "{0}" && scons -C "{1}" platform=windows target=editor arch=x86_64 lto=none -j{2}' -f $VcVarsPath,$source,$Jobs
& cmd.exe /d /s /c $nativeCommand *> (Join-Path $output 'native-build.log')
if ($LASTEXITCODE -ne 0) { throw "JVM native build failed; see $output/native-build.log" }
$nativeLibrary = Join-Path $source 'harness/tests/addons/jvm/libs/windows/godot.jvm.windows.editor.x86_64.dll'
Copy-Item -LiteralPath $nativeLibrary -Destination (Join-Path $project 'addons/jvm/libs/windows/godot.jvm.windows.editor.x86_64.dll') -Force
[pscustomobject]@{
    projectRoot = $project
    addonRoot = Join-Path $project 'addons/jvm'
    descriptor = 'addons/jvm/jvm.gdextension'
    probeScript = 'res://probe.gd'
    sourceRevision = '5d39c7e3fbe5be78853837e3c89676e7a92c9ec9'
    archiveSha256 = $expected
    editorLibrarySha256 = (Get-FileHash -LiteralPath $nativeLibrary).Hash.ToLowerInvariant()
    nativeBuild = 'MSVC; optimized; LTO disabled'
}
