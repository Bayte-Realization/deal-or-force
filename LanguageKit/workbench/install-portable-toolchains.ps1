param(
    [ValidateSet('go','zig','rust','odin','w64devkit')][string[]]$Languages=@('go','zig','rust'),
    [string]$Destination=(Join-Path (Split-Path -Parent $PSScriptRoot) '.cache/toolchains')
)
$ErrorActionPreference='Stop'
$kit=Split-Path -Parent $PSScriptRoot
. (Join-Path $kit 'kit-common.ps1')
Assert-KitInstallPath $Destination
$lock=Get-Content (Join-Path $kit 'toolchains.windows.lock.json') -Raw | ConvertFrom-Json
foreach($language in ($Languages | Select-Object -Unique)){
    $asset=@($lock.assets | Where-Object {$_.id -eq $language -or $_.id.StartsWith($language+'-')})
    if($asset.Count -ne 1){throw 'Ambiguous toolchain lock.'}; $asset=$asset[0]
    $root=Get-KitPath $Destination $asset.id
    $suffix=switch($asset.kind){'zip'{'.zip'}'7z-sfx'{'.7z.exe'}default{'/rustup-init.exe'}}
    $archive=Get-KitPath $Destination ($asset.id+$suffix)
    Assert-KitInstallPath $root; Assert-KitInstallPath $archive
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archive) | Out-Null
    if(-not(Test-Path -LiteralPath $archive)){
        $partial=$archive+'.partial'
        Invoke-WebRequest $asset.url -OutFile $partial
        if((Get-FileHash -LiteralPath $partial).Hash -ne $asset.sha256){throw "Checksum mismatch: $($asset.id); partial archive was not executed."}
        Move-Item -LiteralPath $partial -Destination $archive
    }
    if((Get-FileHash -LiteralPath $archive).Hash -ne $asset.sha256){throw "Checksum mismatch: $($asset.id). Existing installation preserved."}
    if($asset.kind -eq 'zip'){
        if(-not(Test-Path -LiteralPath $root)){Expand-Archive -LiteralPath $archive -DestinationPath $root}
    } elseif($asset.kind -eq '7z-sfx'){
        # A 7-Zip self-extractor: unpack it as an archive, never run the downloaded executable.
        $sevenZip=@((Get-Command 7z -ErrorAction SilentlyContinue | ForEach-Object Source), (Join-Path $env:ProgramFiles '7-Zip/7z.exe')) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
        if(-not $sevenZip){throw "Install 7-Zip to unpack $($asset.id); the self-extractor is not executed."}
        if(-not(Test-Path -LiteralPath $root)){ & $sevenZip x $archive "-o$root" -y | Out-Null; if($LASTEXITCODE -ne 0){throw "Unpacking $($asset.id) failed."} }
    } else {
        $beforeCargo=$env:CARGO_HOME; $beforeRustup=$env:RUSTUP_HOME
        try{
            $env:CARGO_HOME=Join-Path $root 'cargo'; $env:RUSTUP_HOME=Join-Path $root 'rustup'
            & $archive -y --no-modify-path --profile minimal --default-toolchain $asset.toolchain
            if($LASTEXITCODE -ne 0){throw 'Private Rust toolchain installation failed.'}
        } finally { $env:CARGO_HOME=$beforeCargo; $env:RUSTUP_HOME=$beforeRustup }
    }
    $exe=Get-KitPath $root $asset.executable
    if(-not(Test-Path -LiteralPath $exe)){throw "Expected portable executable missing: $exe"}
    Write-Host "TOOLCHAIN_READY $exe archive_sha256=$($asset.sha256)"
}
Write-Host 'No system compiler or persistent PATH/environment setting changed.'
