# Builds the UI and publishes Corewright (NativeAOT service and tray, framework-dependent dashboard).
# Local build only: it installs and publishes nothing. A release needs the owner's approval.
param([string]$Out = (Join-Path $PSScriptRoot '..\artifacts\publish'))
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# The NativeAOT linker setup calls vswhere.exe; add it for this process only (never a persistent PATH change).
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $vsInstaller) -and -not ($env:PATH -split ';' -contains $vsInstaller)) { $env:PATH = "$env:PATH;$vsInstaller" }

Push-Location (Join-Path $root 'ui')
try { npm run build; if ($LASTEXITCODE) { throw 'UI build failed' } } finally { Pop-Location }

foreach ($p in 'Corewright.Service', 'Corewright.Tray') {
    dotnet publish (Join-Path $root "src\$p") -c Release -r win-x64 -o $Out -nologo
    if ($LASTEXITCODE) { throw "$p publish failed" }
}
dotnet publish (Join-Path $root 'src\Corewright.Dashboard') -c Release -r win-x64 --self-contained false -o $Out -nologo
# Corewright Updater: the program the admin prompt names when an update installs.
dotnet publish (Join-Path $root 'src\Corewright.Updater') -c Release -r win-x64 --self-contained false -o $Out -nologo
if ($LASTEXITCODE) { throw 'Dashboard publish failed' }

Get-ChildItem $Out -Filter 'Corewright.*.exe' | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
