<#
.SYNOPSIS
    Draws the DFSR Analyser icon and writes it as a multi-resolution .ico.

.DESCRIPTION
    The icon is a blue rounded tile with a rising bar chart (event rates) and a pair of opposed arrows
    across the top (replication between servers). Each size is drawn separately, not scaled down from
    256 px, so the small sizes stay crisp: below 32 px the arrows are left out and the bars snap to
    whole pixels.

    The result is committed (src/DfsrAnalyser.App/DfsrAnalyser.ico), so this only needs re-running when
    the design changes. Every frame is stored as PNG, which Windows Vista and later read.

.PARAMETER Output
    The .ico to write. Defaults to the app project's icon.

.PARAMETER PreviewFolder
    Optional folder to also save each size as a .png, for looking at.
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\DfsrAnalyser.App\DfsrAnalyser.ico'),
    [string]$PreviewFolder
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$tileTop = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x7D, 0xE6)
$tileBottom = [System.Drawing.Color]::FromArgb(255, 0x1F, 0x56, 0xB8)
$white = [System.Drawing.Color]::White

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($r -le 0) { $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h))); return $p }
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile: a 1 px inset at small sizes keeps the corners from touching the edge.
    $inset = if ($s -le 24) { 0.5 } else { $s * 0.04 }
    $tile = New-RoundedRect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ([Math]::Max(1.5, $s * 0.18))
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $tileTop, $tileBottom, 90)
    $g.FillPath($brush, $tile)

    $small = $s -lt 32
    $whiteBrush = New-Object System.Drawing.SolidBrush($white)

    # Bars: four rising (three when small), resting on a common baseline.
    $heights = if ($small) { 0.30, 0.48, 0.66 } else { 0.20, 0.32, 0.27, 0.42 }
    $n = $heights.Count
    $left = $s * 0.20; $right = $s * 0.80; $base = $s * 0.82
    $gapRatio = 0.45
    $barW = ($right - $left) / ($n + ($n - 1) * $gapRatio)
    $gap = $barW * $gapRatio
    for ($i = 0; $i -lt $n; $i++) {
        $x = $left + $i * ($barW + $gap)
        $h = $s * $heights[$i]
        $y = $base - $h
        if ($small) {
            $x = [Math]::Round($x); $y = [Math]::Round($y)
            $w = [Math]::Max(2, [Math]::Round($barW)); $hh = [Math]::Round($base) - $y
            $g.SmoothingMode = 'None'
            $g.FillRectangle($whiteBrush, [single]$x, [single]$y, [single]$w, [single]$hh)
            $g.SmoothingMode = 'AntiAlias'
        }
        else {
            $bar = New-RoundedRect $x $y $barW $h ($barW * 0.18)
            $g.FillPath($whiteBrush, $bar)
        }
    }

    # Replication arrows: right-pointing above, left-pointing below it, across the top of the tile.
    if (-not $small) {
        $pen = New-Object System.Drawing.Pen($white, [single]($s * 0.045))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
        $ax1 = $s * 0.22; $ax2 = $s * 0.78; $head = $s * 0.075
        $y1 = $s * 0.17; $y2 = $s * 0.27
        $g.DrawLine($pen, [single]$ax1, [single]$y1, [single]$ax2, [single]$y1)
        $g.DrawLine($pen, [single]($ax2 - $head), [single]($y1 - $head), [single]$ax2, [single]$y1)
        $g.DrawLine($pen, [single]$ax1, [single]$y2, [single]$ax2, [single]$y2)
        $g.DrawLine($pen, [single]($ax1 + $head), [single]($y2 + $head), [single]$ax1, [single]$y2)
        $pen.Dispose()
    }

    $g.Dispose()
    return $bmp
}

$frames = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewFolder) {
        New-Item -ItemType Directory -Force $PreviewFolder | Out-Null
        $bmp.Save((Join-Path $PreviewFolder "icon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Data = $ms.ToArray() }
}

# ICO: 6-byte header, a 16-byte directory entry per frame, then the PNG data.
$Output = [IO.Path]::GetFullPath($Output)
$fs = [IO.File]::Create($Output)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$f.Data.Length); $w.Write([uint32]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Dispose()
Write-Host ("Wrote {0} ({1} sizes, {2:N0} KB)" -f $Output, $frames.Count, ((Get-Item $Output).Length / 1KB))
