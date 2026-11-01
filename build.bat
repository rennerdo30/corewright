@echo off
setlocal
rem Corewright: stops the running app, rebuilds everything and starts it again.
rem   build.bat            stop, rebuild, start
rem   build.bat --no-start stop and rebuild only

set "ROOT=%~dp0"
sc query Corewright >nul 2>&1
if not errorlevel 1 goto :installed

echo Stopping Corewright...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\stop.ps1"
if errorlevel 1 goto :fail

echo Building Corewright...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\publish.ps1"
if errorlevel 1 goto :fail

if /I "%~1"=="--no-start" exit /b 0
call "%ROOT%start.bat" --skip-check
exit /b 0

:installed
rem Package without stopping the running installed copy; setup performs its graceful upgrade.
pwsh -NoProfile -File "%ROOT%scripts\update-installed.ps1" %1
if errorlevel 1 goto :fail
exit /b 0

:fail
echo.
echo Build failed, see the output above.
pause
exit /b 1
