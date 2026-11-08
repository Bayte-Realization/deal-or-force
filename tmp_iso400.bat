@echo off
cd /d "D:\Godot Projects\deal-or-force-godot-4-8\Tools\SimBench\bin\Release\net8.0"
SimBench.exe --isolation-only 400 > "D:\Godot Projects\deal-or-force-godot-4-8\tmp_logs\isolation-400.log" 2>&1
echo DONE %ERRORLEVEL% >> "D:\Godot Projects\deal-or-force-godot-4-8\tmp_logs\isolation-400.log"
