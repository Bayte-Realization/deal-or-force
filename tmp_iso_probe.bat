@echo off
cd /d "D:\Godot Projects\deal-or-force-godot-4-8\Tools\SimBench\bin\Release\net8.0"
set DOF_ISOLATION_PROBE=1
SimBench.exe --isolation-only 200 > "D:\Godot Projects\deal-or-force-godot-4-8\tmp_logs\isolation-probe.log" 2>&1
echo DONE %ERRORLEVEL% >> "D:\Godot Projects\deal-or-force-godot-4-8\tmp_logs\isolation-probe.log"
