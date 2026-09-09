@echo off
rem Open the editor with a PATH that leads to a real .NET SDK.
rem
rem This machine has two dotnet installations on the system PATH, and the wrong one comes
rem first: `C:\Program Files (x86)\dotnet` is a 32-bit host with no `sdk` folder in it at all.
rem Godot's editor finds a `dotnet` by walking the PATH, so it finds that one, MSBuildLocator
rem reports no .NET SDK on the machine, and everything downstream of that fails to load
rem Microsoft.Build. The visible result is that the editor cannot build the C# any more:
rem
rem     .NET Sdk not found. The required version is '10.0.12'.
rem     Could not load file or assembly 'Microsoft.Build, Version=15.1.0.0 ...'
rem     ERROR: An EditorPlugin build callback failed.
rem
rem and with no build, Play has no assembly to run. Note that `dotnet build` from a shell is
rem unaffected - the shell's own PATH resolution and Godot's are not the same - so the project
rem can compile perfectly from the command line while the editor insists it cannot.
rem
rem The real cure is to put `C:\Program Files\dotnet` ahead of the x86 entry in the system
rem PATH, which needs an elevated shell and is written down in docs/BUILDING.md. Until that is
rem done, launching through this file fixes the PATH for this one process and nothing else.

setlocal

if not defined GODOT_EXE set "GODOT_EXE=C:\GodotEngineNet\Godot_v4.7.2-stable_mono_win64.exe"

if not exist "%GODOT_EXE%" (
    echo Godot not found at "%GODOT_EXE%".
    echo Set GODOT_EXE to the path of Godot_v4.7.2-stable_mono_win64.exe and run this again.
    exit /b 1
)

set "DOTNET_DIR=C:\Program Files\dotnet"
if not exist "%DOTNET_DIR%\sdk" (
    echo No .NET SDK at "%DOTNET_DIR%\sdk".
    echo The editor will not be able to build the C# until one is installed there.
    exit /b 1
)
set "PATH=%DOTNET_DIR%;%PATH%"

rem %~dp0 ends in a backslash, so %~dp0.. is the project root.
"%GODOT_EXE%" -e --path "%~dp0.." %*
