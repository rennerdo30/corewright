# Owner entry point used by build.bat. Agents compile this code but never run it.
param([Parameter(Position=0)][string]$Mode)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package.ps1')
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$setup = Join-Path $root "artifacts/installer/Corewright-$version-setup.exe"
$process = Start-Process -FilePath $setup -ArgumentList '/S' -Verb RunAs -PassThru
Write-Host "Setup PID $($process.Id), timeout 180 s"
try {
    if (!$process.WaitForExit(180000)) { throw 'Setup did not finish within 180 seconds. Check the running installer before retrying.' }
    if ($process.ExitCode) { throw "Setup failed ($($process.ExitCode)); see the installer log." }
} finally { $process.Dispose() }
if ($Mode -ne '--no-start') { & (Join-Path $root 'start.bat') --skip-check }
