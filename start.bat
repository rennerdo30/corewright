@echo off
setlocal
rem Corewright: builds on first run, starts the service, the tray and the dashboard.
rem Close the minimized "Corewright.Service" window to stop it; every change it made is reverted.

set "ROOT=%~dp0"
set "BIN=%ROOT%artifacts\publish"

if exist "%BIN%\Corewright.Service.exe" goto :service
echo Building Corewright for the first time, this takes a minute...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\publish.ps1"
if errorlevel 1 goto :fail

:service
rem Installed as a Windows service? Then just make sure it runs.
sc query Corewright >nul 2>&1
if errorlevel 1 goto :console
sc start Corewright >nul 2>&1
goto :ui

:console
tasklist /FI "IMAGENAME eq Corewright.Service.exe" 2>nul | find /I "Corewright.Service.exe" >nul
if not errorlevel 1 goto :ui
echo Starting the Corewright service. Windows asks for admin rights so it can manage all processes.
powershell -NoProfile -Command "Start-Process -FilePath '%BIN%\Corewright.Service.exe' -WorkingDirectory '%BIN%' -Verb RunAs -WindowStyle Minimized"
timeout /t 2 /nobreak >nul

:ui
start "" "%BIN%\Corewright.Tray.exe"
start "" "%BIN%\Corewright.Dashboard.exe"
exit /b 0

:fail
echo Build failed, see the output above.
pause
exit /b 1
