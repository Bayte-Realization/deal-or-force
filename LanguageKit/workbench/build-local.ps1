param(
    [ValidateSet('rust','zig','go')][string[]]$Languages=@('rust','zig','go'),
    [string]$EngineDirectory='C:/GodotEngineNet 4.8-Beta2',
    [string]$ArtifactDirectory,
    [switch]$Probe
)
$ErrorActionPreference='Stop'
$kit=Split-Path -Parent $PSScriptRoot
. (Join-Path $kit 'kit-common.ps1')
$engine=Get-KitEngine $EngineDirectory
$lock=Read-KitLock $kit
if($engine.Version -ne $lock.engine.version){throw 'Engine differs from kit target.'}
if(-not $ArtifactDirectory){$ArtifactDirectory=Join-Path $kit '.cache/verified-beta2'}
$tools=Join-Path $kit '.cache/toolchains'
$zig=Join-Path $tools 'zig-0.17.0/zig-x86_64-windows-0.17.0/zig.exe'
$go=Join-Path $tools 'go-1.27.3/go/bin/go.exe'
$cargoRoot=Join-Path $tools 'rust/cargo'
foreach($language in ($Languages | Select-Object -Unique)){
    $source=$lock.sources | Where-Object id -eq $language
    $folder=if($language -eq 'rust'){'rust-release'}else{$language}
    $sourcePath=Get-KitSource ([pscustomobject]@{id=$folder;url=$source.url;commit=$source.commit}) (Join-Path $kit '.cache/sources-beta2')
    $parameters=@{GodotExecutable=$engine.Executable;SourceDirectory=$sourcePath;OutputDirectory=(Join-Path $ArtifactDirectory $language)}
    $before=@{}
    foreach($name in @('PATH','CARGO_HOME','RUSTUP_HOME','RUSTUP_TOOLCHAIN','LIB','CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER','GDRUST_GODOT_BIN')){$before[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
    try {
        switch($language){
            'zig'{$parameters.ZigExecutable=$zig}
            'go'{$parameters.GoExecutable=$go; $parameters.CCompiler='"'+$zig+'" cc -target x86_64-windows-gnu'}
            'rust'{
                $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
                if(-not(Test-Path $vswhere)){throw 'Install MSVC C++ build tools separately; this kit does not install Visual Studio.'}
                $vs=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
                if(-not $vs){throw 'MSVC C++ tools missing.'}
                $msvc=Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
                $sdk=Get-ChildItem 'C:/Program Files (x86)/Windows Kits/10/Lib' -Directory | Where-Object {Test-Path (Join-Path $_.FullName 'um/x64/kernel32.lib')} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
                if(-not $sdk){throw 'Windows SDK missing.'}
                $link=Join-Path $msvc.FullName 'bin/Hostx64/x64/link.exe'
                $env:CARGO_HOME=$cargoRoot; $env:RUSTUP_HOME=Join-Path $tools 'rust/rustup'; $env:RUSTUP_TOOLCHAIN='1.99.1'
                $env:PATH=(Join-Path $cargoRoot 'bin')+';'+(Split-Path -Parent $link)+';'+$env:PATH
                $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER=$link
                $env:LIB=(Join-Path $msvc.FullName 'lib/x64')+';'+(Join-Path $sdk.FullName 'um/x64')+';'+(Join-Path $sdk.FullName 'ucrt/x64')
                $parameters.CargoExecutable=Join-Path $cargoRoot 'bin/cargo.exe'
            }
        }
        & (Join-Path $kit "adapters/$language/build.ps1") @parameters
    } finally {foreach($name in $before.Keys){[Environment]::SetEnvironmentVariable($name,$before[$name],'Process')}}
    if($Probe){& (Join-Path $kit "adapters/$language/probe.ps1") -GodotExecutable $engine.Executable -OutputDirectory $parameters.OutputDirectory}
}
