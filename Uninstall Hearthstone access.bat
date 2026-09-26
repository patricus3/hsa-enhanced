@echo off
rem Hearthstone Access for Windows uninstaller: removes the mod and restores the game's own files.
setlocal
net session >nul 2>&1
if errorlevel 1 (
    echo Asking for administrator rights, the game is in Program Files...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Resources\windows\uninstall.ps1"
echo.
echo Press any key to close this window.
pause >nul
