@echo off
setlocal
rem Corewright: starts the service, the tray and the dashboard. Rebuilds first when the sources are newer than the build.
rem Tray -> "Exit Corewright" stops everything again; every change Corewright made is reverted.

set "ROOT=%~dp0"
set "BIN=%ROOT%artifacts\publish"

if /I "%~1"=="--skip-check" goto :service
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\is-stale.ps1"
if not errorlevel 1 goto :service
call "%ROOT%build.bat" --no-start
if errorlevel 1 exit /b 1

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
