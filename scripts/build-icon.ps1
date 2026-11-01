# Renders assets/corewright-mark*.svg with headless Edge and packs assets/corewright.ico (16-256 px, PNG entries).
# Sizes up to 24 px use the simplified small mark so the tray icon stays crisp. Run after changing the SVGs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Resolve-Path (Join-Path $PSScriptRoot '..\assets')
$tmp = Join-Path ([IO.Path]::GetTempPath()) "corewright-icon-$PID"
New-Item -ItemType Directory -Force $tmp | Out-Null
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'

function Render([string]$svg, [string]$png) {
    $html = Join-Path $tmp ((Split-Path $svg -LeafBase) + '.html')
    $uri = ([Uri](Resolve-Path $svg).Path).AbsoluteUri
    "<html><body style='margin:0;background:transparent'><img src='$uri' width='512' height='512'></body></html>" | Set-Content $html -Encoding utf8
    $p = Start-Process $edge -ArgumentList '--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=$tmp\profile",
        '--default-background-color=00000000', '--window-size=512,512', "--screenshot=$png", ([Uri]$html).AbsoluteUri -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(30000)) { Stop-Process -Id $p.Id; throw 'Edge timed out' }
}

function Scale([string]$src, [int]$size) {
    $img = [Drawing.Image]::FromFile($src)
    try {
        $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([Drawing.Color]::Transparent)
        $g.DrawImage($img, 0, 0, $size, $size)
        $g.Dispose()
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        return , $ms.ToArray()
    } finally { $img.Dispose() }
}

$big = Join-Path $tmp 'big.png'; $small = Join-Path $tmp 'small.png'
Render (Join-Path $assets 'corewright-mark.svg') $big
Render (Join-Path $assets 'corewright-mark-small.svg') $small

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (Scale ($(if ($s -le 24) { $small } else { $big })) $s) }

$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $assets 'corewright.ico'), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path $assets 'corewright-256.png'), $images[-1])
[IO.File]::WriteAllBytes((Join-Path $assets 'corewright-16.png'), $images[0])
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
"wrote $(Join-Path $assets 'corewright.ico') ($($out.Length) bytes, sizes $($sizes -join ', '))"
