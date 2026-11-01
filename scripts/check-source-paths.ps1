# Focused, non-elevated check of inert file-path fixtures only. No installer,
# updater, service, user data, network request or executable payload is run.
param([string]$FixtureRoot = ('E:/AgentData/scratch/corewright-paths-' + [guid]::NewGuid().ToString('N')))
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Write-Output "Source commit: $(& git -C $repo rev-parse HEAD) (includes current worktree changes)"
if (Test-Path -LiteralPath $FixtureRoot) { throw 'Fixture root must be new.' }
$root = [IO.Path]::GetFullPath($FixtureRoot)
$null = New-Item -ItemType Directory -Path $root
Write-Output "Inert fixtures retained: $root"
Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $repo 'installer/secure-files.cs')))
function Assert([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "FAILED: $Message" }
    Write-Output "ok: $Message"
}
function Reject([string]$Path, [string]$Message) {
    $rejected = $false
    try { $pin = [SecureFiles+PinnedPath]::new($Path, $false, 2147483648); $pin.Dispose() }
    catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -isnot [IO.IOException] -and $cause -isnot [ComponentModel.Win32Exception]) { throw }
        $rejected = $true
    }
    Assert $rejected $Message
}
foreach ($name in 'first', 'second', 'normal') { $null = New-Item -ItemType Directory -Path (Join-Path $root $name) }
$first = Join-Path $root 'first'
$second = Join-Path $root 'second'
$alias = Join-Path $root 'updates'
[IO.File]::WriteAllText((Join-Path $first 'payload.txt'), 'verified')
[IO.File]::WriteAllText((Join-Path $second 'payload.txt'), 'replacement')
$null = New-Item -ItemType Junction -Path $alias -Target $first
$source = Join-Path $alias 'payload.txt'
$old = [IO.FileStream]::new($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    # Reproduce the old leaf-only lock: redirect only our own junction, without
    # removing either target directory or touching its data.
    Assert (([IO.File]::GetAttributes($alias) -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'fixture is a junction'
    [IO.Directory]::Delete($alias, $false)
    $null = New-Item -ItemType Junction -Path $alias -Target $second
    Assert ([IO.File]::ReadAllText($source) -ceq 'replacement') 'old leaf-only verification permits ancestor redirection'
} finally { $old.Dispose() }
Reject $source 'hardened traversal rejects a junction ancestor'
$hardlink = Join-Path $root 'hardlink.txt'
$null = New-Item -ItemType HardLink -Path $hardlink -Target (Join-Path $first 'payload.txt')
Reject $hardlink 'hardened traversal rejects multiply-linked files'
$normal = Join-Path $root 'normal'
$file = Join-Path $normal 'payload.txt'
[IO.File]::WriteAllText($file, 'safe')
$pin = [SecureFiles+PinnedPath]::new($file, $false, 2147483648)
try {
    $blocked = $false
    try { [IO.Directory]::Move($normal, (Join-Path $root 'moved')) } catch { $blocked = $true }
    Assert $blocked 'held ancestor handles prevent directory replacement'
    $stream = [IO.FileStream]::new($pin.Leaf, [IO.FileAccess]::Read)
    try { Assert ($stream.ReadByte() -eq 115) 'validated handle reads the original inert bytes' }
    finally { $stream.Dispose() }
} finally { $pin.Dispose() }
# Check the native enumeration/rename layouts without running machine migration.
$pin = [SecureFiles+PinnedPath]::new($normal, $false, 2147549184)
try {
    $flags = [Reflection.BindingFlags]'NonPublic,Static'
    $children = [SecureFiles].GetMethod('Children', $flags).Invoke($null, @($pin.Leaf))
    Assert (@($children).Count -eq 1) 'handle-based directory enumeration sees only the fixture file'
    $renamed = Join-Path $root 'renamed'
    $null = [SecureFiles].GetMethod('Rename', $flags).Invoke($null, @($pin.Leaf, $renamed.ToString()))
    Assert ([IO.File]::ReadAllText((Join-Path $renamed 'payload.txt')) -ceq 'safe') 'handle rename preserves the original object'
} finally { $pin.Dispose() }
foreach ($parent in [Environment]::GetFolderPath('ProgramFiles'), [Environment]::GetFolderPath('CommonApplicationData')) {
    $pin = [SecureFiles+PinnedPath]::new($parent, $true, 0)
    $pin.Dispose()
    Write-Output 'ok: standard protected machine ancestor ACL accepted (read-only)'
}
