param(
    [Parameter(Mandatory)][string]$GodotExecutable,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PythonExecutable = 'python',
    [switch]$SkipDependencies,
    [switch]$PreflightOnly
)
# py4godot - Python scripting for Godot through a Cython GDExtension (MIT).
# Recipe only: see README.md. Upstream calls itself an early-phase demo; the prebuilt release
# zip is usually the better choice for anyone who is not changing py4godot itself.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../workbench/probe-common.ps1')
$pin = 'b41ddb82af5f8e59ce4e1a9254bec9cf82a138dd'
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((& git -C $SourceDirectory rev-parse HEAD) -ne $pin) { throw "py4godot source is not the reviewed pin $pin." }

$python = Get-Command $PythonExecutable -ErrorAction SilentlyContinue
if (-not $python) {
    throw ("python is not on PATH. py4godot needs Python 3.11+ with pip; install it or pass " +
           "-BuildOptions @{ PythonExecutable = 'C:/Python311/python.exe' }. Nothing was built.")
}
$rawVersion = ((& $python.Source --version) -join ' ').Trim()      # "Python 3.11.9"
if ($rawVersion -notmatch '(?<major>\d+)\.(?<minor>\d+)') { throw "Could not read the Python version from: $rawVersion" }
$major = [int]$Matches.major; $minor = [int]$Matches.minor
if ($major -lt 3 -or ($major -eq 3 -and $minor -lt 11)) { throw "py4godot needs Python 3.11 or newer; found $rawVersion." }

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($PreflightOnly) {
    Write-Output "PYTHON_PREFLIGHT OK pin=$pin python=$rawVersion source=$SourceDirectory"
    Write-Output 'WOULD_RUN=venv + pip install -r requirements.txt; generate.py; cythonize_files.py; build.py --target_platform=windows64 --compiler=msvc'
    return
}

# Never install into the interpreter the caller handed us: upstream recommends a virtual
# environment, and a kit that edits somebody's global site-packages is a kit nobody runs twice.
$venv = Join-Path $OutputDirectory 'venv'
$py = Join-Path $venv 'Scripts/python.exe'
if (-not (Test-Path -LiteralPath $py)) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    & $python.Source -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "python -m venv failed: $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $py)) { throw "The virtual environment did not produce $py." }

Push-Location -LiteralPath $SourceDirectory
try {
    if (-not $SkipDependencies) {
        & $py -m pip install --disable-pip-version-check -r requirements.txt
        if ($LASTEXITCODE -ne 0) { throw "pip install -r requirements.txt failed: $LASTEXITCODE" }
    }
    & $py generate.py
    if ($LASTEXITCODE -ne 0) { throw "generate.py failed: $LASTEXITCODE" }
    & $py cythonize_files.py
    if ($LASTEXITCODE -ne 0) { throw "cythonize_files.py failed: $LASTEXITCODE" }
    & $py build.py --target_platform=windows64 --compiler=msvc
    if ($LASTEXITCODE -ne 0) { throw "build.py failed: $LASTEXITCODE" }
} finally { Pop-Location }

$built = Join-Path $SourceDirectory 'build/py4godot'
if (-not (Test-Path -LiteralPath $built)) {
    throw ("build.py returned 0 but produced no addon at $built. The upstream layout has probably " +
           "moved: fix this recipe rather than reporting an artifact that does not exist.")
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $OutputDirectory 'project'
$addon = Join-Path $project 'addons/py4godot'
New-Item -ItemType Directory -Force -Path $addon | Out-Null
Copy-Item -Path (Join-Path $built '*') -Destination $addon -Recurse -Force
New-KitGodotProject $GodotExecutable $project 'Python source ABI probe'
Copy-KitTemplate (Join-Path $PSScriptRoot 'dof_python_kernel.py') (Join-Path $project 'dof_python_kernel.py')
Copy-KitTemplate (Join-Path $PSScriptRoot 'probe-runtime.gd') (Join-Path $project 'probe.gd')
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'LICENSE') -Destination $addon -Force
Write-Output "ADDON_ROOT=$addon"
Write-Output "PROBE_PROJECT=$project"
Write-Output "PYTHON_VERSION=$rawVersion"
