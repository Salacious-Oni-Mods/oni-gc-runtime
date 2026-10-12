@echo off
rem Puts the game's own mono-2.0-bdwgc.dll back. See README.txt; install.ps1 does the work.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
echo.
pause
