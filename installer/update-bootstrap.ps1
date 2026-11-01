# Embedded in the installed dashboard. Invoked in-memory with administrator consent;
# no script, checksum, executable or command is loaded from a user-writable path.
$ErrorActionPreference = 'Stop'
try {
    Add-Type -TypeDefinition $SecureFilesSource
    Add-Type -AssemblyName System.Net.Http
    if ($UpdateVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[0-9A-Za-z.-]+)?$') {
        throw 'Only a stable Corewright version may be installed.'
    }
    $parent = [Environment]::GetFolderPath('ProgramFiles')
    $ancestors = [SecureFiles+PinnedPath]::new($parent, $true, 0)
    try {
        $stage = Join-Path $parent ('Corewright-update-' + [guid]::NewGuid().ToString('N'))
        [SecureFiles]::CreateProtectedDirectory($stage, $false)
        $name = "Corewright-$UpdateVersion-setup.exe"
        $setup = Join-Path $stage $name
        $url = "https://github.com/rennerdo30/corewright/releases/download/v$UpdateVersion"
        # Fetch the expected hash inside the trusted process; caller/cache hashes are untrusted.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $client = [Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromMinutes(5)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('Corewright-Updater')
        function Download([string]$Url, [IO.Stream]$Output, [long]$Limit) {
            $response = $client.GetAsync($Url, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                $null = $response.EnsureSuccessStatusCode()
                $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                try {
                    $buffer = New-Object byte[] 65536
                    $total = 0L
                    $deadline = [DateTime]::UtcNow.AddMinutes(5)
                    while ($true) {
                        if ([DateTime]::UtcNow -gt $deadline) { throw 'Update download timed out.' }
                        $read = $input.ReadAsync($buffer, 0, $buffer.Length)
                        if (!$read.Wait(30000)) { throw 'Update download stalled.' }
                        $count = $read.GetAwaiter().GetResult()
                        if (!$count) { break }
                        $total += $count
                        if ($total -gt $Limit) { throw 'Update download exceeds its size limit.' }
                        $Output.Write($buffer, 0, $count)
                    }
                } finally { $input.Dispose() }
            } finally { $response.Dispose() }
        }
        try {
            $sums = [IO.MemoryStream]::new()
            try {
                Download "$url/SHA256SUMS.txt" $sums 65536
                $lines = [Text.Encoding]::UTF8.GetString($sums.ToArray()).Split("`n")
                $hashes = @($lines | ForEach-Object {
                    if ($_.TrimEnd("`r") -cmatch '^([0-9a-fA-F]{64}) [ *](.+)$' -and $Matches[2] -ceq $name) { $Matches[1] }
                })
                if ($hashes.Count -ne 1) { throw 'Setup checksum is missing or duplicated.' }
            } finally { $sums.Dispose() }
            $created = [SecureFiles]::CreateProtectedFile($setup)
            try {
                $file = [IO.FileStream]::new($created, [IO.FileAccess]::Write)
                try { Download "$url/$name" $file 268435456 } finally { $file.Dispose() }
            } finally { $created.Dispose() }
        } finally { $client.Dispose() }
        $pinned = [SecureFiles+PinnedPath]::new($setup, $true, 2147483648)
        try {
            $file = [IO.FileStream]::new($pinned.Leaf, [IO.FileAccess]::Read)
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($sha.ComputeHash($file)).Replace('-', '') }
            finally { $sha.Dispose() } # Keep file and all ancestors pinned through process exit.
            if ($digest -cne $hashes[0].ToUpperInvariant()) { throw 'Setup SHA-256 mismatch.' }
            $info = [Diagnostics.ProcessStartInfo]::new($setup, '/S')
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $process = [Diagnostics.Process]::Start($info)
            Write-Output "Corewright setup PID $($process.Id) (timeout 180 s)"
            try {
                if (!$process.WaitForExit(180000)) { throw 'Setup timed out; inspect the installer log.' }
                if ($process.ExitCode) { throw "Setup failed ($($process.ExitCode))." }
            } finally { $process.Dispose() }
        } finally {
            if ($file) { $file.Dispose() }
            $pinned.Dispose()
        }
        # Protected stage is retained for diagnosis; no recursive cleanup of external paths.
    } finally { $ancestors.Dispose() }
    exit 0
} catch {
    # No credentials or bearer tokens are handled by this bootstrap.
    Write-Error $_
    exit 1
}
