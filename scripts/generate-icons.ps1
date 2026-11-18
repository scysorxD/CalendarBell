<#
.SYNOPSIS
    Regenerates CalendarBell's tray/app icons (normal, warning, paused) from vector drawing code.
.DESCRIPTION
    Development-time helper only; the app never runs this script. Output:
      src/CalendarBell.App/Resources/Icons/calendarbell.ico          (16-256 px)
      src/CalendarBell.App/Resources/Icons/calendarbell-warning.ico
      src/CalendarBell.App/Resources/Icons/calendarbell-paused.ico
      src/CalendarBell.App/Resources/Icons/calendarbell.png          (256 px, notification registration)
    Small frames (<= 64 px) are stored as 32-bit DIBs for maximum shell compatibility; 256 px as PNG.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\src\CalendarBell.App\Resources\Icons')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-BellPath {
    # Designed on a 256 x 256 grid.
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $p.StartFigure()
    $p.AddBezier(128, 40, 82, 40, 60, 76, 60, 118)
    $p.AddLine(60, 118, 60, 158)
    $p.AddBezier(60, 158, 60, 172, 48, 182, 34, 190)
    $p.AddLine(34, 190, 222, 190)
    $p.AddBezier(222, 190, 208, 182, 196, 172, 196, 158)
    $p.AddLine(196, 158, 196, 118)
    $p.AddBezier(196, 118, 196, 76, 174, 40, 128, 40)
    $p.CloseFigure()
    $p.AddEllipse(114, 18, 28, 28)                            # knob
    $p.AddPath((New-RoundedRectPath 26 184 204 22 11), $false) # lip
    $p.AddEllipse(106, 206, 44, 36)                           # clapper
    return $p
}

function New-Frame([int]$size, [string]$variant) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 256.0
    $g.ScaleTransform($scale, $scale)

    $bellColor = switch ($variant) {
        'paused' { [System.Drawing.Color]::FromArgb(255, 128, 134, 145) }
        default  { [System.Drawing.Color]::FromArgb(255, 37, 99, 235) }
    }
    $outline = [System.Drawing.Color]::FromArgb(255, 18, 46, 120)
    if ($variant -eq 'paused') { $outline = [System.Drawing.Color]::FromArgb(255, 70, 74, 82) }

    $bell = New-BellPath
    $brush = [System.Drawing.SolidBrush]::new($bellColor)
    $g.FillPath($brush, $bell)
    # A thin outline keeps the glyph legible on both light and dark taskbars.
    $penWidth = [Math]::Max(6.0, 1.2 / $scale)
    $pen = [System.Drawing.Pen]::new($outline, $penWidth)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $bell)

    if ($variant -ne 'normal') {
        $badgeColor = if ($variant -eq 'warning') { [System.Drawing.Color]::FromArgb(255, 245, 158, 11) } else { [System.Drawing.Color]::FromArgb(255, 55, 65, 81) }
        $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $g.FillEllipse($white, 132, 132, 124, 124)
        $g.FillEllipse([System.Drawing.SolidBrush]::new($badgeColor), 142, 142, 104, 104)
        if ($variant -eq 'warning') {
            $g.FillPath($white, (New-RoundedRectPath 183 158 22 48 10))
            $g.FillEllipse($white, 182, 212, 24, 24)
        } else {
            $g.FillPath($white, (New-RoundedRectPath 168 166 20 56 8))
            $g.FillPath($white, (New-RoundedRectPath 200 166 20 56 8))
        }
    }
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write([int]($w * $h * 4 + $maskStride * $h))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    for ($y = $h - 1; $y -ge 0; $y--) {
        $row = [byte[]]::new($maskStride)
        for ($x = 0; $x -lt $w; $x++) {
            if ($bmp.GetPixel($x, $y).A -eq 0) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    return $ms.ToArray()
}

function Write-Ico([string]$path, [string]$variant) {
    $images = foreach ($s in $sizes) {
        $frame = New-Frame $s $variant
        if ($s -ge 256) {
            $ms = [System.IO.MemoryStream]::new()
            $frame.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            , @($s, $ms.ToArray())
        } else {
            , @($s, (Get-DibBytes $frame))
        }
        $frame.Dispose()
    }
    $fs = [System.IO.File]::Create($path)
    $bw = [System.IO.BinaryWriter]::new($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $s = $img[0]; $data = $img[1]
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$data.Length); $bw.Write([int]$offset)
        $offset += $data.Length
    }
    foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
    $bw.Dispose()
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Write-Ico (Join-Path $OutputDirectory 'calendarbell.ico') 'normal'
Write-Ico (Join-Path $OutputDirectory 'calendarbell-warning.ico') 'warning'
Write-Ico (Join-Path $OutputDirectory 'calendarbell-paused.ico') 'paused'
$png = New-Frame 256 'normal'
$png.Save((Join-Path $OutputDirectory 'calendarbell.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()
Write-Host "Icons written to $OutputDirectory"
