param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$PythonExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../workbench/probe-common.ps1')
$source=(Resolve-Path -LiteralPath $SourceDirectory).Path
if((& git -C $source rev-parse HEAD) -ne '4f585652a76d3161a5beca4b41559eeb940fcf1d'){throw 'Unexpected godot4-meson revision.'}
$python=(Get-Command $PythonExecutable -ErrorAction Stop).Source
$version=(& $python --version | Out-String).Trim()
if($version -notmatch '^Python 3\.(\d+)' -or [int]$Matches[1] -lt 14){throw "The branch development manifest requires Python 3.14+, found $version."}
$patch=(Resolve-Path (Join-Path $PSScriptRoot 'patches/0001-beta2-api-metadata.patch')).Path
& git -C $source apply --check $patch 2>$null
if($LASTEXITCODE -eq 0){ & git -C $source apply $patch; if($LASTEXITCODE -ne 0){throw 'Patch application failed.'} }
else { & git -C $source apply --reverse --check $patch 2>$null; if($LASTEXITCODE -ne 0){throw 'Source does not match reviewed patch or preimage.'} }
$output=New-KitProbeWorkspace $OutputDirectory $source 'python-meson-review'
New-KitGodotProject $GodotExecutable $output 'Python API review'
# Keep native/runtime work out of this gate. Use the exact local engine, not Meson's
# default downloaded Godot 4.4.1. No C compiler, pip or dependency install is run.
Push-Location -LiteralPath $output
try { $null=Invoke-KitGodot $GodotExecutable @('--headless','--path',$output,'--dump-extension-api') } finally { Pop-Location }
$api=Join-Path $output 'extension_api.json'
if(-not(Test-Path $api)){throw 'Engine did not write extension_api.json.'}
& $python (Join-Path $PSScriptRoot 'tests/test_api_compat.py') --source $source --api $api
if($LASTEXITCODE -ne 0){throw 'Python API regression tests failed.'}
Write-Host 'PYTHON_MESON_API_PASS runtime_verified=false native_build_verified=false'
