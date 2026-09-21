# Renders the KAM Capture Tool mark to PNGs and packs a multi-resolution .ico.
# Pure GDI+ so it needs nothing installed beyond Windows PowerShell.
[CmdletBinding()]
param(
    [string]$OutIco,
    [string]$OutPng
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutIco) { $OutIco = Join-Path $root '..\src\KamCapture\Assets\kam-capture.ico' }
if (-not $OutPng) { $OutPng = Join-Path $root '..\assets\branding\kam-capture-icon.png' }

Add-Type -AssemblyName System.Drawing

$Navy   = [System.Drawing.ColorTranslator]::FromHtml('#0A0D14')
$Accent = [System.Drawing.ColorTranslator]::FromHtml('#4A7CFF')
$Deep   = [System.Drawing.ColorTranslator]::FromHtml('#3A63D8')
$Pale   = [System.Drawing.ColorTranslator]::FromHtml('#9CBEFF')

function New-Mark {
    param([int]$S)

    $u = $S / 256.0
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # Dark disc, same as the rest of the KAM family.
    $bgBrush = New-Object System.Drawing.SolidBrush($Navy)
    $g.FillEllipse($bgBrush, 0.0, 0.0, [float]($S - 1), [float]($S - 1))

    # Four corner brackets: the selection frame.
    $sw = [float](17 * $u)
    $pen = New-Object System.Drawing.Pen($Accent, $sw)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $i = 58.0 * $u      # inset from the icon edge
    $a = 38.0 * $u      # arm length
    $f = $S - $i

    # Round caps mean two lines per corner render identically to a joined path.
    # Top-left
    $g.DrawLine($pen, [float]$i, [float]($i + $a), [float]$i, [float]$i)
    $g.DrawLine($pen, [float]$i, [float]$i, [float]($i + $a), [float]$i)
    # Top-right
    $g.DrawLine($pen, [float]($f - $a), [float]$i, [float]$f, [float]$i)
    $g.DrawLine($pen, [float]$f, [float]$i, [float]$f, [float]($i + $a))
    # Bottom-left
    $g.DrawLine($pen, [float]$i, [float]($f - $a), [float]$i, [float]$f)
    $g.DrawLine($pen, [float]$i, [float]$f, [float]($i + $a), [float]$f)
    # Bottom-right
    $g.DrawLine($pen, [float]($f - $a), [float]$f, [float]$f, [float]$f)
    $g.DrawLine($pen, [float]$f, [float]$f, [float]$f, [float]($f - $a))

    # The pencil, drawn on a rotated axis so the nib points down-left.
    $state = $g.Save()
    $g.TranslateTransform([float](128 * $u), [float](128 * $u))
    $g.RotateTransform(135.0)

    $bodyBrush = New-Object System.Drawing.SolidBrush($Deep)
    $paleBrush = New-Object System.Drawing.SolidBrush($Pale)
    $navyBrush = New-Object System.Drawing.SolidBrush($Navy)

    $h = [float](15 * $u)
    # Barrel
    $g.FillRectangle($bodyBrush, [float](-54 * $u), -$h, [float](76 * $u), [float](30 * $u))
    # Ferrule
    $g.FillRectangle($paleBrush, [float](22 * $u), -$h, [float](7 * $u), [float](30 * $u))
    # Nib
    $nib = @(
        (New-Object System.Drawing.PointF([float](29 * $u), -$h)),
        (New-Object System.Drawing.PointF([float](29 * $u), $h)),
        (New-Object System.Drawing.PointF([float](58 * $u), 0.0))
    )
    $g.FillPolygon($paleBrush, [System.Drawing.PointF[]]$nib)
    # Graphite point
    $tip = @(
        (New-Object System.Drawing.PointF([float](49 * $u), [float](-4.7 * $u))),
        (New-Object System.Drawing.PointF([float](49 * $u), [float](4.7 * $u))),
        (New-Object System.Drawing.PointF([float](58 * $u), 0.0))
    )
    $g.FillPolygon($navyBrush, [System.Drawing.PointF[]]$tip)

    $g.Restore($state)

    $pen.Dispose(); $bgBrush.Dispose(); $bodyBrush.Dispose(); $paleBrush.Dispose(); $navyBrush.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Mark -S $s
    $frames += [pscustomobject]@{ Size = $s; Bytes = (Get-PngBytes -Bitmap $bmp) }
    if ($s -eq 256) {
        $dir = Split-Path -Parent $OutPng
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
}

$icoDir = Split-Path -Parent $OutIco
if (-not (Test-Path $icoDir)) { New-Item -ItemType Directory -Path $icoDir -Force | Out-Null }

$fs = [System.IO.File]::Create($OutIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                    # reserved
$bw.Write([uint16]1)                    # type: icon
$bw.Write([uint16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim)               # width
    $bw.Write([byte]$dim)               # height
    $bw.Write([byte]0)                  # palette count
    $bw.Write([byte]0)                  # reserved
    $bw.Write([uint16]1)                # colour planes
    $bw.Write([uint16]32)               # bits per pixel
    $bw.Write([uint32]$f.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()

Write-Host "Icon written: $OutIco ($([math]::Round((Get-Item $OutIco).Length / 1kb, 1)) KB, $($frames.Count) sizes)"
Write-Host "PNG written:  $OutPng"
