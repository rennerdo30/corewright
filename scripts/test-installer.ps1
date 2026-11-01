# Destructive lifecycle smoke: disposable elevated Windows x64 CI runners only.
# Never invoke on the owner's PC. Does not launch the tray or dashboard.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
if (!$env:CI) { throw 'Runs only on disposable CI runners (installs a LocalSystem service).' }
if (![Environment]::Is64BitProcess) { throw 'Use x64 PowerShell.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'CI must already be elevated; this script never requests elevation.' }
$dir = Join-Path $env:ProgramFiles 'Corewright'
$data = Join-Path $env:ProgramData 'Corewright'
$userData = Join-Path $env:LOCALAPPDATA 'Corewright'
$key = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Corewright'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Corewright.lnk'
foreach ($path in $dir, $data, $userData, $key, $shortcut) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to touch an existing installation or data: $path" }
}
if ((Get-Service Corewright -ErrorAction SilentlyContinue) -or (Get-ItemProperty -LiteralPath $runKey -Name Corewright -ErrorAction SilentlyContinue)) { throw 'Corewright service or autostart already exists.' }

function Assert([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "FAILED: $Message" }
    Write-Host "ok: $Message"
}
function Invoke-Bounded([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    Write-Host "Started PID $($process.Id): $File (timeout 180 s)"
    try {
        if (!$process.WaitForExit(180000)) { $process.Kill($true); $process.WaitForExit(); throw "$File timed out" }
        if ($process.ExitCode) { throw "$File exited with $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
# Use a valid empty rule set; compare bytes so service startup must preserve it.
New-Item -ItemType Directory -Path $userData | Out-Null
$rules = Join-Path $userData 'rules.json'
Set-Content -LiteralPath $rules -Value '{"rules":[]}' -NoNewline -Encoding utf8
$sourceHash = (Get-FileHash -LiteralPath $rules).Hash
Invoke-Bounded $Setup @('/S')
foreach ($file in 'Corewright.Service.exe', 'Corewright.Tray.exe', 'Corewright.Dashboard.exe', 'Corewright.Dashboard.dll', 'Corewright.Dashboard.runtimeconfig.json', 'appsettings.json', 'corewright.ico', 'wwwroot/index.html', 'uninstall.exe', 'install-manifest.txt') {
    Assert (Test-Path -LiteralPath (Join-Path $dir $file)) "installed $file"
}
$service = Get-CimInstance Win32_Service -Filter "Name='Corewright'"
Assert ($service.State -eq 'Running') 'service is running'
Assert ($service.StartName -eq 'LocalSystem') 'service runs as LocalSystem'
Assert ($service.StartMode -eq 'Auto') 'service starts automatically'
Assert ($service.PathName -eq ('"' + (Join-Path $dir 'Corewright.Service.exe') + '"')) 'service executable path is quoted'
$entry = Get-ItemProperty -LiteralPath $key
Assert ($entry.DisplayName -eq 'Corewright' -and $entry.DisplayVersion -eq $Version) 'Installed apps name and version'
Assert ($entry.Publisher -eq 'rennerdo30' -and $entry.InstallLocation -eq $dir) 'Installed apps publisher and location'
Assert ($entry.QuietUninstallString -eq ('"' + (Join-Path $dir 'uninstall.exe') + '" /S')) 'silent uninstall command'
Assert ($entry.EstimatedSize -gt 0 -and $entry.NoModify -eq 1 -and $entry.NoRepair -eq 1) 'Installed apps size and flags'
Assert ((Get-ItemPropertyValue -LiteralPath $runKey -Name Corewright) -eq ('"' + (Join-Path $dir 'Corewright.Tray.exe') + '"')) 'per-user tray autostart'
Assert (Test-Path -LiteralPath $shortcut) 'all-users Start menu shortcut'
$shell = New-Object -ComObject WScript.Shell
Assert ($shell.CreateShortcut($shortcut).TargetPath -eq (Join-Path $dir 'Corewright.Dashboard.exe')) 'shortcut opens dashboard'
Assert ((Get-FileHash -LiteralPath (Join-Path $data 'rules.json')).Hash -eq $sourceHash) 'console rules migrated unchanged'
# Upgrade must stop/restart the existing service and keep machine rules, even
# when console rules have subsequently changed.
Set-Content -LiteralPath $rules -Value '{"rules":[],"ciMarker":"different"}' -NoNewline -Encoding utf8
Invoke-Bounded $Setup @('/S')
Assert ((Get-Service Corewright).Status -eq 'Running') 'upgrade restarted the service'
Assert ((Get-FileHash -LiteralPath (Join-Path $data 'rules.json')).Hash -eq $sourceHash) 'upgrade never overwrites machine rules'
$sentinel = Join-Path $data 'ci-preserve.txt'
Set-Content -LiteralPath $sentinel -Value 'keep'
# _?= runs the uninstaller in place, so WaitForExit covers the actual uninstall
# instead of only the parent that spawns a temporary copy. It must be last.
$uninstaller = Join-Path $dir 'uninstall.exe'
Invoke-Bounded $uninstaller @('/S', "_?=$dir")
Assert (!(Get-Service Corewright -ErrorAction SilentlyContinue)) 'service removed'
Assert (!(Test-Path -LiteralPath $key)) 'Installed apps entry removed'
Assert (!(Get-ItemProperty -LiteralPath $runKey -Name Corewright -ErrorAction SilentlyContinue)) 'autostart removed'
Assert (!(Test-Path -LiteralPath $shortcut)) 'shortcut removed'
Assert (!(Test-Path -LiteralPath (Join-Path $dir 'Corewright.Service.exe'))) 'service executable removed'
Assert (!(Test-Path -LiteralPath (Join-Path $dir 'wwwroot'))) 'dashboard assets removed'
Assert ((Get-Content -LiteralPath $sentinel -Raw).Trim() -eq 'keep') 'ProgramData survives silent uninstall'
Assert ((Get-FileHash -LiteralPath (Join-Path $data 'rules.json')).Hash -eq $sourceHash) 'rules survive silent uninstall'
# In-place uninstall cannot delete its own running executable. Remove only that
# known file after its recorded PID exits, then require the directory to be empty.
Remove-Item -LiteralPath $uninstaller -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir }
Assert (!(Test-Path -LiteralPath $dir)) 'install folder removed'
exit 0
