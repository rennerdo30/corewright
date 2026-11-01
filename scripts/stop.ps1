# Stops a Corewright started by start.bat: dashboard, tray and console-mode service, each through its own exit path
# (named event, window message, API). Nothing is killed by name. An installed Windows service is left alone.
$ErrorActionPreference = 'Stop'

# Dashboard: the window closes itself when this event is set.
try {
    $ev = [Threading.EventWaitHandle]::OpenExisting('Local\Corewright.Dashboard.Exit')
    $null = $ev.Set(); $ev.Dispose()
} catch [Threading.WaitHandleCannotBeOpenedException] { }

# Tray: WM_CLOSE to its hidden window ends the message loop and removes the icon.
Add-Type -Namespace Corewright -Name Win -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
'@
$tray = [Corewright.Win]::FindWindowW('CorewrightTray', $null)
if ($tray -ne [IntPtr]::Zero) { $null = [Corewright.Win]::PostMessageW($tray, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }

# Console-mode service: graceful shutdown over the API (it reverts every change it made).
$servicePid = $null
$endpoint = Join-Path $env:LOCALAPPDATA 'Corewright\endpoint.json'
if (Test-Path $endpoint) {
    $ep = Get-Content $endpoint -Raw | ConvertFrom-Json
    if ($ep.mode -eq 'console') {
        $servicePid = $ep.pid
        try {
            Invoke-RestMethod "$($ep.url)/api/v1/app/shutdown" -Method Post -Headers @{ Authorization = "Bearer $($ep.token)" } -TimeoutSec 3 | Out-Null
        } catch {
            Write-Host 'The service did not answer; it may already be stopped.'
        }
    }
}

$svc = Get-Service -Name Corewright -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Host 'Note: the installed Corewright Windows service keeps running. Stop it as admin (sc stop Corewright) before rebuilding over its files.'
}

# Wait (max 15 s) until the service process has exited so its files are no longer locked.
if ($servicePid) {
    for ($i = 0; $i -lt 60 -and (Get-Process -Id $servicePid -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
    if (Get-Process -Id $servicePid -ErrorAction SilentlyContinue) { throw "The Corewright service (pid $servicePid) did not stop. Close its window and try again." }
}
Start-Sleep -Milliseconds 500
Write-Host 'Corewright stopped.'
