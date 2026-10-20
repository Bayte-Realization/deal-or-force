Set-StrictMode -Version Latest

function Get-KitPath {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Relative)
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[\\/])\.\.([\\/]|$)' -or $Relative.Contains(':')) {
        throw "Path must stay relative to its declared root: $Relative"
    }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $candidate = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $candidate.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes its declared root: $Relative"
    }
    return $candidate
}

function Read-KitLock {
    param([string]$KitRoot = $PSScriptRoot)
    $data = Get-Content -LiteralPath (Join-Path $KitRoot 'sources.lock.json') -Raw | ConvertFrom-Json
    if ($data.schema_version -ne 1) { throw 'Unsupported source lock schema.' }
    $ids = @{}
    foreach ($source in $data.sources) {
        if ($source.id -notmatch '^[a-z][a-z0-9_-]*$' -or $ids.ContainsKey($source.id)) { throw 'Invalid or duplicated source id.' }
        if ($source.commit -notmatch '^[0-9a-f]{40}$' -or $source.url -notmatch '^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git$') {
            throw "Invalid pin or source URL for $($source.id)."
        }
        $ids[$source.id] = $true
    }
    $adapterIds = @{}
    foreach ($adapter in $data.adapters) {
        if ($adapter.id -notmatch '^[a-z][a-z0-9_-]*$' -or $adapterIds.ContainsKey($adapter.id) -or ($adapter.source -and -not $ids.ContainsKey($adapter.source))) {
            throw 'Invalid adapter/source reference.'
        }
        $adapterIds[$adapter.id] = $true
    }
    return $data
}

function Get-KitEngine {
    param([Parameter(Mandatory)][string]$EngineDirectory)
    $directory = (Resolve-Path -LiteralPath $EngineDirectory).Path
    $matches = @(Get-ChildItem -LiteralPath $directory -File -Filter '*mono*console.exe')
    if ($matches.Count -ne 1) { throw 'Select one .NET Godot installation containing exactly one mono console executable.' }
    $exe = $matches[0].FullName
    $version = @(& $exe --headless --version) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Godot version query failed.' }
    return [pscustomobject]@{ Directory = $directory; Executable = $exe; Version = $version.Trim() }
}

function Invoke-KitGit {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & git @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "git failed with exit code $LASTEXITCODE; existing source is preserved." }
}

function Assert-KitInstallPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/') -or
        $full -eq [Environment]::GetFolderPath('UserProfile').TrimEnd('\', '/')) {
        throw 'A kit destination must be a dedicated directory, not a drive or user-profile root.'
    }
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Install target traverses a reparse point: $current" }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}

function Get-KitSource {
    param([Parameter(Mandatory)]$Source, [Parameter(Mandatory)][string]$CacheDirectory)
    $path = Get-KitPath $CacheDirectory $Source.id
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
        Invoke-KitGit @('-c', 'core.longpaths=true', 'clone', '--filter=blob:none', '--no-checkout', $Source.url, $path)
        Invoke-KitGit @('-C', $path, 'fetch', '--depth', '1', 'origin', $Source.commit)
        Invoke-KitGit @('-C', $path, 'checkout', '--detach', $Source.commit)
    }
    $head = @(& git -C $path rev-parse HEAD) -join ''
    if ($LASTEXITCODE -ne 0 -or $head.Trim() -ne $Source.commit) { throw "Existing $($Source.id) checkout differs from its pin; it was not reset." }
    $url = @(& git -C $path config --get remote.origin.url) -join ''
    if ($LASTEXITCODE -ne 0 -or $url.Trim().TrimEnd('/') -ne $Source.url) { throw "Unexpected upstream for $($Source.id); existing checkout preserved." }
    Write-Host "SOURCE $($Source.id) $($Source.commit)"
    return $path
}

function Get-KitSourceFiles {
    param([Parameter(Mandatory)][string]$Root)
    $excluded = @('.git', '.cache', 'artifacts', 'upstream', 'projects', 'bin', 'obj', '.gradle', '.godot', 'target', 'build')
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Kit source contains a reparse point: $($item.FullName)" }
        if ($item.PSIsContainer) {
            if ($item.Name -notin $excluded) { Get-KitSourceFiles $item.FullName }
        } elseif ($item.Name -notlike '*.local.json') {
            if ($item.Extension -in @('.exe', '.dll', '.so', '.dylib', '.jar', '.zip', '.pck', '.pdb')) {
                throw "Binary artifact in source kit: $($item.FullName)"
            }
            $item
        }
    }
}

function Install-KitSources {
    param([Parameter(Mandatory)][string]$KitRoot, [Parameter(Mandatory)][string]$Destination)
    $root = [IO.Path]::GetFullPath($KitRoot).TrimEnd('\', '/')
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/')
    if ($root -eq $destinationRoot -or $destinationRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Install the kit outside its source directory.'
    }
    Assert-KitInstallPath $destinationRoot
    $files = @(Get-KitSourceFiles $root)
    $plan = @($files | ForEach-Object {
        $relative = $_.FullName.Substring($root.Length + 1)
        $target = Get-KitPath $destinationRoot $relative
        Assert-KitInstallPath $target
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) {
                throw "Existing installed file differs; preserve it or choose a fresh kit directory: $target"
            }
        }
        [pscustomobject]@{ Source = $_.FullName; Target = $target; Relative = $relative; SHA256 = $hash }
    })
    foreach ($file in $plan) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file.Target) | Out-Null
        if (-not (Test-Path -LiteralPath $file.Target)) { Copy-Item -LiteralPath $file.Source -Destination $file.Target }
    }
    Write-Host "Installed $($plan.Count) source files. No engine executable, project settings or native DLL was overwritten."
    return $plan
}

function Invoke-KitGodot {
    param(
        [Parameter(Mandatory)][string]$GodotExecutable,
        [Parameter(Mandatory)][string[]]$Arguments,
        [ValidateRange(5, 300)][int]$TimeoutSeconds = 90
    )
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = (Resolve-Path -LiteralPath $GodotExecutable).Path
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -gt $deadline) {
            # This exact process was created here. Never enumerate/stop other Godot editors.
            $process.Kill()
            throw "Timed out after $TimeoutSeconds seconds; stopped only kit-owned PID $($process.Id)."
        }
    }
    $text = $stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()
    $code = $process.ExitCode
    $process.Dispose()
    Write-Host $text.Trim()
    if ($code -ne 0 -or $text -match '(?m)(?:^|\s)(?:SCRIPT ERROR:|USER ERROR:|ERROR:|Parse Error:)') {
        throw "Godot probe/import failed: exit=$code. Error output is not ignored."
    }
    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}

function Invoke-KitEditorBootstrap {
    param([Parameter(Mandatory)][string]$GodotExecutable, [Parameter(Mandatory)][string]$ProjectDirectory)
    $project = (Resolve-Path -LiteralPath $ProjectDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $project 'project.godot'))) { throw 'Missing probe project.' }
    Initialize-KitExtensionCache $project
    # Godot #111048: cold GDExtension documentation callbacks may outlive an
    # immediate --import shutdown. This is a bounded, documented bootstrap,
    # not suppression of an error/exit code or a claim to have patched the engine.
    $null = Invoke-KitGodot $GodotExecutable @('--headless', '--editor', '--path', $project, '--frame-delay', '20', '--quit-after', '300')
    $null = Invoke-KitGodot $GodotExecutable @('--headless', '--editor', '--import', '--path', $project)
    Write-Host 'EDITOR_BOOTSTRAP cold_exit=0 warm_import_exit=0 errors=0'
}

function Initialize-KitExtensionCache {
    param([Parameter(Mandatory)][string]$ProjectDirectory)
    $project = (Resolve-Path -LiteralPath $ProjectDirectory).Path.TrimEnd('\', '/')
    $list = [Collections.Generic.List[string]]::new()
    $cache = Join-Path $project '.godot/extension_list.cfg'
    if (Test-Path -LiteralPath $cache) {
        foreach ($line in Get-Content -LiteralPath $cache) {
            if ($line.Trim() -and -not $list.Contains($line.Trim())) { $list.Add($line.Trim()) }
        }
    }
    $addons = Join-Path $project 'addons'
    if (Test-Path -LiteralPath $addons) {
        foreach ($file in Get-ChildItem -LiteralPath $addons -Recurse -File -Filter '*.gdextension') {
            $relative = [IO.Path]::GetRelativePath($project, $file.FullName).Replace('\', '/')
            $null = Get-KitPath $project $relative
            $resourcePath = 'res://' + $relative
            if (-not $list.Contains($resourcePath)) { $list.Add($resourcePath) }
        }
    }
    # Derived cache, not project configuration. Declaring known descriptors before
    # first scan lets language extensions initialize before editor UI construction.
    New-Item -ItemType Directory -Path (Split-Path -Parent $cache) -Force | Out-Null
    [IO.File]::WriteAllLines($cache, $list, [Text.UTF8Encoding]::new($false))
}
