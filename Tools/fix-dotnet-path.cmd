@echo off
rem Double-click this. Windows will ask for administrator rights once, and the editor will
rem be able to build the C# again after Godot is restarted.
rem
rem What it does and why is written at the top of fix-dotnet-path.ps1, which is worth
rem reading before running anything that asks for administrator rights - including this.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0fix-dotnet-path.ps1"
