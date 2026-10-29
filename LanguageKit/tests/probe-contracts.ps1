param([string]$KitRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
. (Join-Path $KitRoot 'workbench/probe-common.ps1')
$checks=0
function Check($condition,$label) { if(-not $condition){throw "FAIL $label"}; $script:checks++; Write-Host "PASS $label" }
function Reject([scriptblock]$action,$label) { $rejected=$false; try { & $action | Out-Null } catch { $rejected=$true }; Check $rejected $label }
$temp=Join-Path ([IO.Path]::GetTempPath()) ('languagekit-probes-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $source=Join-Path $temp 'source'; New-Item -ItemType Directory $source | Out-Null
    $output=Join-Path $temp 'output'
    Check ((New-KitProbeWorkspace $output $source 'zig') -eq $output) 'fresh owned workspace'
    Check (Test-Path (Join-Path $output '.gdignore')) 'scratch excluded from editor scan'
    Check ((New-KitProbeWorkspace $output $source 'zig') -eq $output) 'same adapter may rebuild'
    Reject {New-KitProbeWorkspace $output $source 'go'} 'different adapter cannot overwrite workspace'
    Reject {New-KitProbeWorkspace $source $source 'zig'} 'source cannot become output'
    Reject {New-KitProbeWorkspace (Join-Path $source 'nested') $source 'zig'} 'output cannot be inside source'
    $unowned=Join-Path $temp 'unowned'; New-Item -ItemType Directory $unowned | Out-Null
    Reject {New-KitProbeWorkspace $unowned $source 'zig'} 'unowned directory preserved'
    $target=Join-Path $output 'template.txt'
    Copy-KitTemplate -Target $target -Content 'one'
    Copy-KitTemplate -Target $target -Content 'one'
    Check ([IO.File]::ReadAllText($target) -eq 'one') 'template creation and idempotence'
    Reject {Copy-KitTemplate -Target $target -Content 'two'} 'different template never overwrites user edit'
    Check ([IO.File]::ReadAllText($target) -eq 'one') 'rejected overwrite preserves bytes'
    $archive=Join-Path $temp 'invalid.zip'; [IO.File]::WriteAllText($archive,'not a release')
    Reject { & (Join-Path $KitRoot 'adapters/python/prepare-release.ps1') -GodotExecutable 'not-run' -OutputDirectory (Join-Path $temp 'python') -Archive $archive } 'wrong Python archive rejected before executing code'
    foreach($id in @('go','zig')) {
        $probe=[IO.File]::ReadAllText((Join-Path $KitRoot "adapters/$id/probe.gd"))
        Check ($probe.Contains('ClassDB.instantiate') -and $probe.Contains('weighted_sum')) "$id requires real native invocation"
    }
    $errors=$null; $tokens=$null
    foreach($file in Get-KitSourceFiles $KitRoot | Where-Object Extension -eq '.ps1') {
        [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
        Check ($errors.Count -eq 0) "PowerShell parses: $([IO.Path]::GetRelativePath($KitRoot,$file.FullName))"
    }
} finally {
    $resolved=[IO.Path]::GetFullPath($temp)
    if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved) -like 'languagekit-probes-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
Write-Host "PROBE_CONTRACTS checks=$checks failures=0"
