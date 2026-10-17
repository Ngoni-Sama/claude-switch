<#
.SYNOPSIS
    Draws the Claude Switch icon and writes it in every format the packages need.

.DESCRIPTION
    Original artwork (not Anthropic's logo): a dark rounded tile with a
    neumorphic toggle switch, the knob in the accent colour, and two small
    status dots for "two accounts".

    Outputs (committed to the repo, so CI on macOS/Linux doesn't need this):
      src/ClaudeSwitch/Assets/icon.png   256 px (window + tray icon)
      src/ClaudeSwitch/Assets/icon.ico   16-256 px (Windows exe + installer)
      packaging/macos/icon.icns          128-1024 px
      packaging/linux/claude-switch.png  512 px
      docs/assets/icon-512.png, docs/assets/favicon.png
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function New-Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $k = $s / 1024.0

    # Tile: charcoal with a soft top-left highlight.
    $tile = New-Rounded (40*$k) (40*$k) (944*$k) (944*$k) (220*$k)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s),
        ([System.Drawing.Color]::FromArgb(255, 0x3A, 0x3F, 0x48)), ([System.Drawing.Color]::FromArgb(255, 0x1F, 0x22, 0x27))
    $g.FillPath($grad, $tile)

    # Switch track: an inset pill.
    $tx = 170*$k; $ty = 362*$k; $tw = 684*$k; $th = 300*$k
    $track = New-Rounded $tx $ty $tw $th ($th/2)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1B, 0x1D, 0x22))), $track)
    $edge = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF $tx, $ty), (New-Object System.Drawing.PointF ($tx+$tw), ($ty+$th)),
        ([System.Drawing.Color]::FromArgb(255, 0x10, 0x11, 0x14)), ([System.Drawing.Color]::FromArgb(255, 0x4A, 0x50, 0x5A))
    $g.DrawPath((New-Object System.Drawing.Pen $edge, ([float](10*$k))), $track)

    # Two account dots on the left of the track.
    foreach ($dot in @(@(0x63, 0xD6, 0x93, 262), @(0xF2, 0xC1, 0x4E, 372))) {
        $dx = $dot[3]*$k; $dy = 512*$k; $dr = 34*$k
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, $dot[0], $dot[1], $dot[2]))), [float]($dx-$dr), [float]($dy-$dr), [float]($dr*2), [float]($dr*2))
    }

    # Knob on the right: accent orange with a glow.
    $kx = 690*$k; $ky = 542*$k; $kr = 128*$k
    for ($i = 12; $i -ge 1; $i--) {
        $gr = $kr + $i * 7*$k
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(9, 0xEC, 0x97, 0x66))), [float]($kx-$gr), [float]($ky-$gr-30*$k), [float]($gr*2), [float]($gr*2))
    }
    $knob = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ($kx-$kr), ($ky-$kr)), (New-Object System.Drawing.PointF ($kx+$kr), ($ky+$kr)),
        ([System.Drawing.Color]::FromArgb(255, 0xF6, 0xB0, 0x86)), ([System.Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x46))
    $g.FillEllipse($knob, [float]($kx-$kr), [float]($ky-$kr-30*$k), [float]($kr*2), [float]($kr*2))
    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, [string]$path) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Png-Bytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $b = $ms.ToArray(); $ms.Dispose(); return ,$b
}

# --- PNGs -------------------------------------------------------------------
$big = Draw-Icon 1024
Save-Png (Draw-Icon 256) (Join-Path $root 'src\ClaudeSwitch\Assets\icon.png')
Save-Png (Draw-Icon 512) (Join-Path $root 'packaging\linux\claude-switch.png')
Save-Png (Draw-Icon 512) (Join-Path $root 'docs\assets\icon-512.png')
Save-Png (Draw-Icon 64)  (Join-Path $root 'docs\assets\favicon.png')

# --- ICO: PNG-compressed entries (Vista+) -------------------------------------
$sizes = 16, 24, 32, 48, 64, 128, 256
$imgs = foreach ($sz in $sizes) { ,(Png-Bytes (Draw-Icon $sz)) }
$ico = Join-Path $root 'src\ClaudeSwitch\Assets\icon.ico'
$fs = [System.IO.File]::Open($ico, 'Create'); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$imgs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $imgs[$i].Length
}
foreach ($b in $imgs) { $bw.Write($b) }
$bw.Close()

# --- ICNS: PNG entries ic07 (128) ic08 (256) ic09 (512) ic10 (1024) ------------
function Write-BE32($w, [int]$v) { $w.Write([byte](($v -shr 24) -band 255)); $w.Write([byte](($v -shr 16) -band 255)); $w.Write([byte](($v -shr 8) -band 255)); $w.Write([byte]($v -band 255)) }
$entries = @(@('ic07', 128), @('ic08', 256), @('ic09', 512), @('ic10', 1024)) | ForEach-Object { ,@($_[0], (Png-Bytes (Draw-Icon $_[1]))) }
$total = 8 + ($entries | ForEach-Object { 8 + $_[1].Length } | Measure-Object -Sum).Sum
$icns = Join-Path $root 'packaging\macos\icon.icns'
$fs = [System.IO.File]::Open($icns, 'Create'); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([System.Text.Encoding]::ASCII.GetBytes('icns')); Write-BE32 $bw $total
foreach ($e in $entries) { $bw.Write([System.Text.Encoding]::ASCII.GetBytes($e[0])); Write-BE32 $bw (8 + $e[1].Length); $bw.Write($e[1]) }
$bw.Close()

Write-Host "Icons written: icon.png, icon.ico ($($sizes -join ',')), icon.icns, linux + docs PNGs" -ForegroundColor Green
