@echo off
rem Installs the parallel-marking mono-2.0-bdwgc.dll. See README.txt; install.ps1 does the work.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
