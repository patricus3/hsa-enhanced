@echo off
rem Hearthstone Access Enhanced for Windows installer. Builds the mod on this PC from your installed
rem game; speech goes through Prism to your screen reader. Hearthstone Access itself is not needed.
rem The work is done by Resources\windows\install.ps1; this file only asks for administrator rights.
setlocal
net session >nul 2>&1
if errorlevel 1 (
    echo Asking for administrator rights, the game is in Program Files...
    if "%~1"=="" (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    ) else (
        powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%*' -Verb RunAs"
    )
    exit /b
)
if not exist "%~dp0Resources\windows\install.ps1" (
    echo ERROR: the Resources folder was not found next to this file.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Resources\windows\install.ps1" %*
echo.
echo Press any key to close this window.
pause >nul
