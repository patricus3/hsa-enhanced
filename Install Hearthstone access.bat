@echo off
rem Hearthstone Access for Windows installer (unofficial). Builds the mod on this PC from your
rem installed game and the official HSA release; speech goes through Prism to your screen reader.
rem The work is done by Resources\windows\install.ps1; this file only asks for administrator rights.
rem   --use-hsa-menus   use Hearthstone Access's own menus instead of the enhanced menu system
rem   --without-hsa     only our own core, without Hearthstone Access (friends list, popups; most screens are not ours yet)
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
