param(
    [string]$EngineDirectory='C:/GodotEngineNet 4.8-Beta2',
    [string]$ArtifactDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) '.cache/verified-beta2'),
    [ValidateSet('rust','zig','go','python-release')][string[]]$Languages=@('rust','zig','go')
)
$ErrorActionPreference='Stop'
$kit=Split-Path -Parent $PSScriptRoot
. (Join-Path $kit 'workbench/probe-common.ps1')
$engine=Get-KitEngine $EngineDirectory
if($engine.Version -ne (Read-KitLock $kit).engine.version){throw 'Choose the exact engine targeted by this kit.'}
$results=@()
$logs=Join-Path $ArtifactDirectory ('verification-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $logs | Out-Null
foreach($language in ($Languages | Select-Object -Unique)){
    $output=Join-Path $ArtifactDirectory $language
    $log=Join-Path $logs ($language+'.log')
    $passed=$false; $reason=''
    try {
        if($language -eq 'python-release'){
            Test-KitScalarAdapter $engine.Executable $output 'PYTHON' *>&1 | Tee-Object -FilePath $log | Out-Host
        } else {
            & (Join-Path $kit "adapters/$language/probe.ps1") -GodotExecutable $engine.Executable -OutputDirectory $output *>&1 | Tee-Object -FilePath $log | Out-Host
        }
        $passed=$true
    } catch {
        $reason=$_.Exception.Message
        Add-Content -LiteralPath $log -Value ("REJECTED: "+$reason)
        Write-Host "$language REJECTED: $reason"
    }
    $results += [pscustomobject]@{language=$language;engine=$engine.Version;runtime_verified=$passed;reason=$reason;log=$log}
}
[IO.File]::WriteAllText((Join-Path $logs 'results.json'),($results | ConvertTo-Json -Depth 4))
Write-Host "VERIFICATION_RESULTS=$logs"
if(@($results | Where-Object {-not $_.runtime_verified}).Count){exit 1}
