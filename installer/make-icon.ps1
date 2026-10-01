# Draws the orange spark icon and writes a multi-size app.ico (PNG-compressed entries).
param([string]$Out = (Join-Path $PSScriptRoot '..\app.ico'))
Add-Type -AssemblyName System.Drawing

function New-SparkPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    # Dark rounded tile
    $r = $size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2; $m = $size - 1
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($m - $d, 0, $d, $d, 270, 90)
    $path.AddArc($m - $d, $m - $d, $d, $d, 0, 90); $path.AddArc(0, $m - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 24, 28))), $path)
    # Four rotated rounded bars = spark
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x57)), ($size * 0.13)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $c = $size / 2; $len = $size * 0.30
    foreach ($a in 0, 45, 90, 135) {
        $rad = $a * [Math]::PI / 180
        $dx = [Math]::Sin($rad) * $len; $dy = [Math]::Cos($rad) * $len
        $g.DrawLine($pen, [float]($c - $dx), [float]($c - $dy), [float]($c + $dx), [float]($c + $dy))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) { , (New-SparkPng $s) }
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | Join-Path -ChildPath (Split-Path $Out -Leaf)))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset); $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Wrote $Out"

# macOS app icon next to it: .icns with PNG entries (ic07 = 128, ic08 = 256, ic09 = 512); lengths are big-endian
function BE([uint32]$v) { $b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); , $b }
$icns = [System.IO.Path]::ChangeExtension((Resolve-Path -LiteralPath (Split-Path $Out) | Join-Path -ChildPath (Split-Path $Out -Leaf)), '.icns')
$entries = foreach ($e in @('ic07', 128), @('ic08', 256), @('ic09', 512)) { , @($e[0], (New-SparkPng $e[1])) }
$total = 8; foreach ($e in $entries) { $total += 8 + $e[1].Length }
$w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($icns))
$w.Write([Text.Encoding]::ASCII.GetBytes('icns')); $w.Write((BE $total))
foreach ($e in $entries) { $w.Write([Text.Encoding]::ASCII.GetBytes($e[0])); $w.Write((BE (8 + $e[1].Length))); $w.Write($e[1]) }
$w.Close()
Write-Host "Wrote $icns"
