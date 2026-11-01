# Exit code 1 when the published build is missing or older than any source file, 0 when it is current.
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$exe = Join-Path $root 'artifacts\publish\Corewright.Service.exe'
if (-not (Test-Path $exe)) { exit 1 }
$built = (Get-Item $exe).LastWriteTimeUtc

$sources = @('src', 'ui\src', 'assets') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } |
    ForEach-Object { Get-ChildItem $_ -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj|wwwroot)\\' } }
$sources += Get-ChildItem $root -File -Include 'Directory.Build.props', 'Corewright.slnx' -Recurse:$false -ErrorAction SilentlyContinue
$sources += Get-Item (Join-Path $root 'ui\index.html'), (Join-Path $root 'ui\package.json') -ErrorAction SilentlyContinue

$newest = $sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($newest -and $newest.LastWriteTimeUtc -gt $built) {
    Write-Host "Sources changed since the last build ($($newest.Name)); rebuilding."
    exit 1
}
exit 0
