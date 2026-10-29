param(
    [ValidateSet('Doctor', 'Fetch', 'Build', 'Probe', 'InstallKit')][string]$Action = 'Doctor',
    [string]$EngineDirectory = 'C:/GodotEngineNet 4.8-Beta2',
    # The six reviewed languages. zig, go, python, lua and sandbox are opt-in: ask for them by
    # name (`-Languages zig`) even though their pins and recipes ship in this kit.
    [string[]]$Languages = @('luau', 'fsharp', 'nim', 'odin', 'rust', 'kotlin'),
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '.cache/sources'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '.cache/artifacts'),
    [string]$InstallDirectory,
    [string]$JavaHome = $env:JAVA_HOME,
    [hashtable]$BuildOptions = @{}
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'kit-common.ps1')
$lock = Read-KitLock $PSScriptRoot
$engine = Get-KitEngine $EngineDirectory
if ($engine.Version -ne $lock.engine.version) {
    throw "This kit targets $($lock.engine.version), not $($engine.Version). Revalidate against the selected engine first."
}
$selected = @($Languages | Select-Object -Unique | ForEach-Object {
    $id = $_
    $adapter = @($lock.adapters | Where-Object { $_.id -eq $id })
    if ($adapter.Count -ne 1) { throw "Unknown adapter: $id" }
    $adapter[0]
})
switch ($Action) {
    'Doctor' {
        Write-Host "ENGINE $($engine.Version)"
        Write-Host "EXECUTABLE $($engine.Executable)"
        Write-Host "DIRECTORY $($engine.Directory)"
        foreach ($name in @('git', 'dotnet', 'cmake', 'ninja', 'uv', 'cargo', 'rustc', 'nim', 'odin', 'java', 'gradle',
                            'zig', 'go', 'gcc', 'python', 'meson', 'pip')) {
            $tool = Get-Command $name -ErrorAction SilentlyContinue
            if ($tool) { Write-Host "$name $($tool.Source)" } else { Write-Host "$name NOT_ON_PATH (portable compiler parameters are supported)" }
        }
        # Every adapter in the lock, not only the selected ones: knowing what is available is
        # the point of a doctor, and a language left out of -Languages is still worth seeing.
        foreach ($adapter in $lock.adapters) {
            $verifiedProperty = $adapter.PSObject.Properties['verified']
            $verified = if ($verifiedProperty) { [bool]$verifiedProperty.Value } else { $true }
            $statusProperty = $adapter.PSObject.Properties['status']
            $status = if ($statusProperty) { [string]$statusProperty.Value } else { '' }
            $requiresProperty = $adapter.PSObject.Properties['requires']
            $requires = if ($requiresProperty) { @($requiresProperty.Value) } else { @() }
            # Historical Beta1 results do not turn into Beta2 verification when
            # the engine pin changes. Only exact-engine evidence gets this label.
            $evidence = if ($lock.PSObject.Properties['verification']) { $lock.verification.PSObject.Properties[$adapter.id] } else { $null }
            $verdict = if ($evidence -and $evidence.Value.engine -eq $engine.Version) {
                if ($evidence.Value.stage -eq 'runtime-smoke') { 'RUNTIME-SMOKE-VERIFIED' }
                else { 'REJECTED-' + $evidence.Value.stage }
            } elseif ($verified) { 'HISTORICAL-ONLY (Beta1; selected engine unverified)' } else { 'RECIPE-ONLY' }
            $line = "ADAPTER $($adapter.id) $($adapter.integration) $verdict"
            if ($status.Length -gt 0) { $line += " -- $status" }
            Write-Host $line
            foreach ($requirement in $requires) {
                $found = Get-Command $requirement -ErrorAction SilentlyContinue
                if ($found -and $requirement -eq 'go') {
                    $raw = (& $found.Source version | Out-String).Trim()
                    if ($raw -match 'go1\.(\d+)' -and [int]$Matches[1] -ge 27) { Write-Host "  requires go READY $raw $($found.Source)" }
                    else { Write-Host "  requires go INCOMPATIBLE $raw (needs 1.27+; portable toolchain can be supplied)" }
                }
                elseif ($found -and $requirement -eq 'zig') {
                    $raw = (& $found.Source version | Out-String).Trim()
                    if ($raw -eq '0.17.0') { Write-Host "  requires zig READY $raw $($found.Source)" }
                    else { Write-Host "  requires zig INCOMPATIBLE $raw (needs exactly 0.17.0)" }
                }
                elseif ($found) { Write-Host "  requires $requirement READY $($found.Source)" }
                else { Write-Host "  requires $requirement MISSING (install it, or pass its path to -BuildOptions)" }
            }
        }
        Write-Host 'ADAPTER presence is not proof: build/probe evidence lives in reports/, and a successful build alone is not a runtime pass.'
    }
    'Fetch' {
        foreach ($adapter in $selected) {
            if ($adapter.source) {
                $source = $lock.sources | Where-Object { $_.id -eq $adapter.source }
                Get-KitSource $source $CacheDirectory
            } else { Write-Host "$($adapter.id) uses the included managed template; no upstream fork is needed." }
        }
    }
    'Build' {
        foreach ($adapter in $selected) {
            $script = Get-KitPath $PSScriptRoot "adapters/$($adapter.id)/build.ps1"
            if (-not (Test-Path -LiteralPath $script)) { throw "No reviewed build recipe for $($adapter.id); see its report." }
            $sourceDirectory = Get-KitPath $PSScriptRoot "adapters/$($adapter.id)/template"
            if ($adapter.source) {
                $source = $lock.sources | Where-Object { $_.id -eq $adapter.source }
                $sourceDirectory = Get-KitSource $source $CacheDirectory
            }
            $parameters = @{ SourceDirectory = $sourceDirectory; OutputDirectory = (Get-KitPath $OutputDirectory $adapter.id) }
            $contract = Get-Command $script
            if ($contract.Parameters.ContainsKey('GodotExecutable')) { $parameters.GodotExecutable = $engine.Executable }
            if ($contract.Parameters.ContainsKey('JavaHome') -and $JavaHome) { $parameters.JavaHome = $JavaHome }
            foreach ($key in $BuildOptions.Keys) {
                if ($key -in @('GodotExecutable', 'SourceDirectory', 'OutputDirectory')) { throw "Use the explicit kit options, not BuildOptions.$key." }
                $parameters[$key] = $BuildOptions[$key]
            }
            & $script @parameters
            if (-not $?) { throw "$($adapter.id) build failed." }
        }
    }
    'Probe' {
        foreach ($adapter in $selected) {
            $script = Get-KitPath $PSScriptRoot "adapters/$($adapter.id)/probe.ps1"
            if (-not (Test-Path -LiteralPath $script)) { throw "No reviewed probe for $($adapter.id)." }
            $artifact = Get-KitPath $OutputDirectory $adapter.id
            $contract = Get-Command $script
            $parameters = @{ GodotExecutable = $engine.Executable }
            if ($contract.Parameters.ContainsKey('OutputDirectory')) { $parameters.OutputDirectory = $artifact }
            elseif ($contract.Parameters.ContainsKey('AddonDirectory')) {
                $parameters.AddonDirectory = $artifact
                $parameters.ProjectDirectory = Get-KitPath $OutputDirectory ("probes/" + $adapter.id + '-' + [guid]::NewGuid().ToString('N'))
            } else {
                $project = Join-Path $artifact 'project'
                $parameters.ProjectDirectory = if (Test-Path -LiteralPath (Join-Path $project 'project.godot')) { $project } else { $artifact }
            }
            if ($contract.Parameters.ContainsKey('JavaHome')) {
                if (-not $JavaHome) { throw 'Pass -JavaHome for the Kotlin probe.' }
                $parameters.JavaHome = $JavaHome
            }
            $probeOutput = @(& $script @parameters 6>&1 2>&1)
            $probeOutput | ForEach-Object { Write-Host $_ }
            if (($probeOutput -join "`n") -match '(?m)(?:^|\s)(?:SCRIPT ERROR:|USER ERROR:|ERROR:|Parse Error:)') {
                throw "$($adapter.id) probe emitted Godot errors; exit 0 alone is insufficient."
            }
        }
    }
    'InstallKit' {
        if (-not $InstallDirectory) { $InstallDirectory = Join-Path $engine.Directory 'LanguageKit' }
        Install-KitSources $PSScriptRoot $InstallDirectory | Select-Object Relative, SHA256
    }
}
