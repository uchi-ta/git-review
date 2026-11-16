# Generates the application icon (tools\GitReview.ico).
#   Taskbar and Explorer cannot always read PNG entries at small sizes, so every
#   size up to 128px is written as BMP(DIB) and only 256px as PNG.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $here 'GitReview.ico'

function Add-RoundedRect($path, [double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $d = $r * 2
    $path.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
    $path.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
    $path.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
    $path.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
    $path.CloseFigure()
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $u = $s / 100.0

    # Background: rounded square in the app's accent blue
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $bg 0 0 $s $s ($s * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 31, 111, 235))), $bg)

    # Two diff bars: one added, one removed
    $added = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $added (16 * $u) (20 * $u) (48 * $u) (13 * $u) (6.5 * $u)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 63, 185, 80))), $added)

    $removed = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundedRect $removed (16 * $u) (40 * $u) (34 * $u) (13 * $u) (6.5 * $u)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 123, 114))), $removed)

    # Check mark: the file has been reviewed
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float][Math]::Max(1.6, 15 * $u))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @(
        (New-Object System.Drawing.PointF ([float](24 * $u), [float](66 * $u))),
        (New-Object System.Drawing.PointF ([float](42 * $u), [float](84 * $u))),
        (New-Object System.Drawing.PointF ([float](88 * $u), [float](34 * $u)))
    )
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $bd = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $px = New-Object byte[] ($bd.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($bd.Scan0, $px, 0, $px.Length)
    $stride = $bd.Stride
    $bmp.UnlockBits($bd)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER (height covers both the XOR and AND planes)
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0); $bw.Write([uint32]0)
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    # XOR plane, bottom-up, BGRA
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($px, $y * $stride, $w * 4) }
    # AND mask: all zero because alpha carries the shape. Rows are 4-byte aligned.
    $maskStride = [int][Math]::Floor(($w + 31) / 32) * 4
    $zero = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($zero, 0, $maskStride) }
    $bw.Flush()
    return , $ms.ToArray()      # leading comma stops PowerShell unrolling the array
}

$entries = @()
foreach ($s in @(16, 20, 24, 32, 40, 48, 64, 96, 128)) {
    $b = New-IconBitmap $s
    $entries += , @($s, (Get-DibBytes $b))
    $b.Dispose()
}
$b = New-IconBitmap 256
$ms = New-Object System.IO.MemoryStream
$b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$entries += , @(256, $ms.ToArray())
$b.Dispose()

$file = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($file)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $sz = $e[0]; $len = $e[1].Length
    $bw.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))
    $bw.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($e in $entries) { $bw.Write([byte[]]$e[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $file.ToArray())
$bw.Dispose()

Write-Host ("OK: {0}  ({1:N0} bytes, {2} entries)" -f $out, (Get-Item $out).Length, $entries.Count)

foreach ($s in @(16, 32, 48, 256)) {
    $i = New-Object System.Drawing.Icon($out, $s, $s)
    Write-Host ("  {0,3}px -> {1}x{2}" -f $s, $i.Width, $i.Height)
    $i.Dispose()
}
