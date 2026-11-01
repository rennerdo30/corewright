# Build only: never installs Corewright or changes persistent PATH.
# Portable compiler: NSIS 3.13, official SourceForge nsis-3.13.zip:
# https://sourceforge.net/projects/nsis/files/NSIS%203/3.13/nsis-3.13.zip/download
# SHA-256 BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8
# Extract to E:/Development/_cache/tools/nsis-3.13/ or pass -MakeNsis.
[CmdletBinding()]
param([string]$MakeNsis = 'E:/Development/_cache/tools/nsis-3.13/makensis.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Write-Host "Source commit: $(& git -C $root rev-parse HEAD) (includes current worktree changes)"
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$') { throw "Unexpected version: $version" }
$numeric = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
if (!(Test-Path -LiteralPath $MakeNsis -PathType Leaf)) { throw "NSIS 3 compiler not found: $MakeNsis. Extract the pinned portable ZIP above or pass -MakeNsis <path>." }
$MakeNsis = (Resolve-Path -LiteralPath $MakeNsis).Path

function Invoke-Bounded([string]$File, [string[]]$Arguments, [int]$Seconds) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $root
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    Write-Host "Started PID $($process.Id): $File (timeout $Seconds s)"
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit($Seconds * 1000)) {
            $process.Kill($true) # Only this invocation and its own descendants.
            $process.WaitForExit()
            throw "$File timed out after $Seconds s"
        }
        Write-Host $stdout.GetAwaiter().GetResult()
        Write-Host $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode) { throw "$File failed ($($process.ExitCode))" }
    } finally { $process.Dispose() }
}

# A fresh worktree has no UI dependencies. Restore from the lockfile, using npm's
# existing shared cache; never install tools globally or run package hooks.
if (!(Test-Path -LiteralPath (Join-Path $root 'ui/node_modules'))) {
    Invoke-Bounded (Join-Path $PSHOME 'pwsh.exe') @('-NoProfile', '-Command', '& npm.cmd ci --prefix ./ui --ignore-scripts --no-audit --no-fund; exit $LASTEXITCODE') 300
}
$publish = Join-Path $root 'artifacts/publish'
Invoke-Bounded (Join-Path $PSHOME 'pwsh.exe') @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'publish.ps1'), '-Out', $publish) 900
foreach ($file in 'Corewright.Service.exe', 'Corewright.Tray.exe', 'Corewright.Dashboard.exe', 'Corewright.Dashboard.dll', 'Corewright.Dashboard.runtimeconfig.json', 'appsettings.json', 'corewright.ico', 'wwwroot/index.html') {
    if (!(Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Publish output lacks $file" }
}
$out = Join-Path $root 'artifacts/installer'
New-Item -ItemType Directory -Force -Path $out | Out-Null
# A new stage never overwrites another run's output; preserve it for inspection.
$stageRoot = Join-Path $out ("stage-" + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $stageRoot 'publish'
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $stage -Recurse

# Exact payload removal, including WebView2 DLLs and wwwroot. Unknown files stay.
# Append each upgrade's entries so files removed from future builds are tracked.
function Quote-Nsis([string]$Value) { $Value.Replace('$', '$$').Replace('"', '$\"') }
$manifest = [Collections.Generic.List[string]]::new()
$preflight = [Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
    $relative = Quote-Nsis ([IO.Path]::GetRelativePath($stage, $file.FullName))
    $manifest.Add('FileWrite $8 "F|' + $relative + '$\r$\n"')
    if ($relative -eq 'Corewright.Service.exe') { $preflight.Add('${If} $ServicePid == 0') }
    $preflight.Add('${If} ${FileExists} "$INSTDIR\' + $relative + '"')
    $preflight.Add('ClearErrors')
    $preflight.Add('FileOpen $7 "$INSTDIR\' + $relative + '" a')
    $preflight.Add('IfErrors locked')
    $preflight.Add('FileClose $7')
    $preflight.Add('${EndIf}')
    if ($relative -eq 'Corewright.Service.exe') { $preflight.Add('${EndIf}') }
}
foreach ($directory in Get-ChildItem -LiteralPath $stage -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
    $relative = Quote-Nsis ([IO.Path]::GetRelativePath($stage, $directory.FullName))
    $manifest.Add('FileWrite $8 "D|' + $relative + '$\r$\n"')
}
$manifest | Set-Content -LiteralPath (Join-Path $stageRoot 'install-manifest.nsh') -Encoding utf8
$preflight | Set-Content -LiteralPath (Join-Path $stageRoot 'preflight-files.nsh') -Encoding utf8
$setup = Join-Path $out "Corewright-$version-setup.exe"
# NSIS is invoked directly, without adding the portable tool to PATH.
Invoke-Bounded $MakeNsis @('-V3', "-DVERSION=$version", "-DVI_VERSION=$numeric", "-DSRC=$stage", "-DOUTFILE=$setup", (Join-Path $root 'installer/corewright.nsi')) 180
$artifact = Get-Item -LiteralPath $setup
Write-Host ('{0:N0} bytes  {1}' -f $artifact.Length, $artifact.FullName)
