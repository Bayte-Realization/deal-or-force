# Put the real .NET SDK ahead of the empty one in the system PATH.
#
# This machine has two dotnet installations on the system PATH and the wrong one comes
# first: `C:\Program Files (x86)\dotnet` is a 32-bit host with no `sdk` folder in it at all.
# Godot's editor resolves `dotnet` by walking the PATH, finds that one, and concludes there
# is no .NET SDK on the machine:
#
#     .NET Sdk not found. The required version is '10.0.12'.
#     Could not load file or assembly 'Microsoft.Build, Version=15.1.0.0, ...'
#     The build method threw an exception.
#
# With no build there is no assembly, and Play refuses to start. A shell resolves the PATH
# differently, which is why `dotnet build` works by hand while the editor insists it cannot.
#
# Only the order changes. Nothing is installed, nothing is removed, and the x86 entry stays
# on the PATH for whatever put it there - it simply stops being asked first.
#
# Run it by double-clicking Tools\fix-dotnet-path.cmd. Windows will ask for administrator
# rights, because the system PATH is a machine-wide setting; there is no way to reorder it
# without them, and no user-level environment variable substitutes (DOTNET_HOST_PATH and
# DOTNET_ROOT were both tried and neither makes any difference).

$ErrorActionPreference = "Stop"

$real = "C:\Program Files\dotnet"

function Write-Step($text) { Write-Host $text -ForegroundColor Cyan }

# ------------------------------------------------------------------ elevate, once
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $elevated) {
    Write-Step "Asking Windows for administrator rights - the system PATH needs them."
    try {
        Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    }
    catch {
        Write-Host ""
        Write-Host "The elevation prompt was refused, so nothing was changed." -ForegroundColor Yellow
        Write-Host "Until the PATH is reordered, open the editor through Tools\godot.cmd," -ForegroundColor Yellow
        Write-Host "which fixes the PATH for that one process and touches nothing else." -ForegroundColor Yellow
        Read-Host "Press Enter to close"
    }
    exit
}

# ------------------------------------------------------------------ look before touching
if (-not (Test-Path (Join-Path $real "sdk"))) {
    Write-Host "There is no SDK at $real\sdk." -ForegroundColor Red
    Write-Host "Install the .NET 8 SDK (or newer) first; reordering the PATH would not help." -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}

$key = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
$path = (Get-ItemProperty -Path $key -Name Path).Path
$entries = @($path -split ';' | Where-Object { $_ -ne "" })

function Same($left, $right) { $left.TrimEnd('\') -ieq $right.TrimEnd('\') }

Write-Step "The dotnet entries in the system PATH now:"
for ($index = 0; $index -lt $entries.Count; $index++) {
    if ($entries[$index] -match 'dotnet') { Write-Host ("  [{0}] {1}" -f $index, $entries[$index]) }
}

$first = ($entries | Where-Object { $_ -match 'dotnet' } | Select-Object -First 1)
if ($first -and (Same $first $real)) {
    Write-Host ""
    Write-Host "The real SDK is already the first dotnet on the PATH. Nothing to do." -ForegroundColor Green
    Write-Host "If the editor still fails to build, it was started before an earlier fix -" -ForegroundColor Green
    Write-Host "close Godot completely and open it again; it reads the PATH once, at startup." -ForegroundColor Green
    Read-Host "Press Enter to close"
    exit
}

# ------------------------------------------------------------------ the one change
# A copy of what it was, in case anything downstream of this ever needs to be put back.
$backup = Join-Path $env:USERPROFILE ("system-path-backup-{0}.txt" -f (Get-Date -Format "yyyy-MM-dd-HHmmss"))
Set-Content -Path $backup -Value $path -Encoding UTF8
Write-Step "The old PATH is saved at $backup"

$reordered = @($real) + @($entries | Where-Object { -not (Same $_ $real) })
# ExpandString on purpose: the system PATH is REG_EXPAND_SZ, and a PATH written as a plain
# string loses the ability to hold a `%SystemRoot%`-style entry - which this machine does not
# have today and might tomorrow.
Set-ItemProperty -Path $key -Name Path -Value ($reordered -join ';') -Type ExpandString

# ------------------------------------------------------------------ tell Windows about it
# Writing the registry changes nothing for anything already running, and that is not a
# footnote: Explorer reads the environment when it starts, hands a copy to every program
# launched from it, and on a machine nobody has restarted this month that copy can be weeks
# old. Closing Godot and opening it again from Explorer therefore gets the *old* PATH, which
# looks exactly like the fix not working, and is how the first version of this script wasted
# an evening. WM_SETTINGCHANGE is the message that asks Explorer to read it again.
Add-Type -Namespace Win32 -Name Env -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam,
    string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
$answer = [UIntPtr]::Zero
[void][Win32.Env]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, "Environment", 2, 5000, [ref]$answer)

Write-Host ""
Write-Host "Done. $real is now the first dotnet on the system PATH," -ForegroundColor Green
Write-Host "and Windows has been told to let running programs know." -ForegroundColor Green
Write-Host ""
Write-Host "Close Godot completely and open it again - a running program keeps the" -ForegroundColor Cyan
Write-Host "environment it started with." -ForegroundColor Cyan
Write-Host ""
Write-Host "If it still cannot build after that, Explorer did not take the hint. Then" -ForegroundColor Yellow
Write-Host "either restart Explorer:" -ForegroundColor Yellow
Write-Host "    Stop-Process -Name explorer -Force" -ForegroundColor Yellow
Write-Host "(the taskbar comes back on its own), or reboot - or simply open the editor" -ForegroundColor Yellow
Write-Host "through Tools\godot.cmd, which never depended on any of this." -ForegroundColor Yellow
Read-Host "Press Enter to close"
