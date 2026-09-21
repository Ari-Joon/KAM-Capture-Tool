# Renders the KAM Capture Tool mark and packs a multi-resolution .ico.
#
# Four variations on the same idea, in the KAM black/white/gold palette that the
# non-security tools use. Pure GDI+, so it needs nothing installed.
#
#   ./scripts/make-icon.ps1                 build the chosen variant
#   ./scripts/make-icon.ps1 -Variant 3      build a different one
#   ./scripts/make-icon.ps1 -Sheet          render all four side by side to compare
[CmdletBinding()]
param(
    [ValidateRange(1, 4)]
    [int]$Variant = 4,
    [switch]$Sheet,
    [string]$OutIco,
    [string]$OutPng
)

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutIco) { $OutIco = Join-Path $root '..\src\KamCapture\Assets\kam-capture.ico' }
if (-not $OutPng) { $OutPng = Join-Path $root '..\assets\branding\kam-capture-icon.png' }

Add-Type -AssemblyName System.Drawing

# ---- KAM black / white / gold ----
$Ink      = [System.Drawing.ColorTranslator]::FromHtml('#0C0C0E')
$Gold     = [System.Drawing.ColorTranslator]::FromHtml('#D9A93A')
$GoldDeep = [System.Drawing.ColorTranslator]::FromHtml('#A9781F')
$GoldPale = [System.Drawing.ColorTranslator]::FromHtml('#F2DB96')
$White    = [System.Drawing.ColorTranslator]::FromHtml('#F7F7F5')

function New-Graphics {
    param([System.Drawing.Bitmap]$Bitmap)
    $g = [System.Drawing.Graphics]::FromImage($Bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return $g
}

# Draw letterspaced text centred on a point; GDI+ has no tracking of its own.
function Draw-Tracked {
    param(
        [System.Drawing.Graphics]$G, [string]$Text, [System.Drawing.Font]$Font,
        [System.Drawing.Brush]$Brush, [single]$CenterX, [single]$CenterY, [single]$Tracking
    )
    $fmt = [System.Drawing.StringFormat]::GenericTypographic
    $widths = @()
    $total = 0.0
    foreach ($ch in $Text.ToCharArray()) {
        $w = $G.MeasureString([string]$ch, $Font, [System.Drawing.PointF]::new(0, 0), $fmt).Width
        $widths += $w
        $total += $w + $Tracking
    }
    $total -= $Tracking

    $h = $G.MeasureString($Text, $Font, [System.Drawing.PointF]::new(0, 0), $fmt).Height
    $x = $CenterX - ($total / 2.0)
    $y = $CenterY - ($h / 2.0)

    for ($i = 0; $i -lt $Text.Length; $i++) {
        $G.DrawString([string]$Text[$i], $Font, $Brush, [single]$x, [single]$y, $fmt)
        $x += $widths[$i] + $Tracking
    }
}

function Draw-Brackets {
    param([System.Drawing.Graphics]$G, [double]$U, [System.Drawing.Color]$Colour,
          [double]$Inset = 58, [double]$Arm = 38, [double]$Weight = 17)

    $pen = New-Object System.Drawing.Pen($Colour, [single]($Weight * $U))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $i = $Inset * $U
    $a = $Arm * $U
    $f = (256 * $U) - $i

    $G.DrawLine($pen, [single]$i, [single]($i + $a), [single]$i, [single]$i)
    $G.DrawLine($pen, [single]$i, [single]$i, [single]($i + $a), [single]$i)
    $G.DrawLine($pen, [single]($f - $a), [single]$i, [single]$f, [single]$i)
    $G.DrawLine($pen, [single]$f, [single]$i, [single]$f, [single]($i + $a))
    $G.DrawLine($pen, [single]$i, [single]($f - $a), [single]$i, [single]$f)
    $G.DrawLine($pen, [single]$i, [single]$f, [single]($i + $a), [single]$f)
    $G.DrawLine($pen, [single]($f - $a), [single]$f, [single]$f, [single]$f)
    $G.DrawLine($pen, [single]$f, [single]$f, [single]$f, [single]($f - $a))
    $pen.Dispose()
}

function Draw-Pencil {
    param([System.Drawing.Graphics]$G, [double]$U, [double]$CX, [double]$CY,
          [double]$Scale = 1.0, [double]$Rotation = 135)

    $state = $G.Save()
    $G.TranslateTransform([single]($CX * $U), [single]($CY * $U))
    $G.RotateTransform([single]$Rotation)

    $body = New-Object System.Drawing.SolidBrush($Gold)
    $pale = New-Object System.Drawing.SolidBrush($White)
    $ink  = New-Object System.Drawing.SolidBrush($Ink)

    $s = $U * $Scale
    $h = [single](15 * $s)
    $G.FillRectangle($body, [single](-54 * $s), -$h, [single](76 * $s), [single](30 * $s))

    # A dark band between barrel and nib, so the shape still reads as a pencil
    # once the whole thing is 32 pixels across.
    $G.FillRectangle($ink, [single](22 * $s), -$h, [single](5 * $s), [single](30 * $s))

    $nib = @(
        (New-Object System.Drawing.PointF([single](27 * $s), -$h)),
        (New-Object System.Drawing.PointF([single](27 * $s), $h)),
        (New-Object System.Drawing.PointF([single](60 * $s), 0.0)))
    $G.FillPolygon($pale, [System.Drawing.PointF[]]$nib)

    $tip = @(
        (New-Object System.Drawing.PointF([single](50 * $s), [single](-4.9 * $s))),
        (New-Object System.Drawing.PointF([single](50 * $s), [single](4.9 * $s))),
        (New-Object System.Drawing.PointF([single](60 * $s), 0.0)))
    $G.FillPolygon($ink, [System.Drawing.PointF[]]$tip)

    $body.Dispose(); $pale.Dispose(); $ink.Dispose()
    $G.Restore($state)
}

function New-Mark {
    param([int]$S, [int]$Style)

    $u = $S / 256.0
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics -Bitmap $bmp
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-Object System.Drawing.SolidBrush($Ink)
    $g.FillEllipse($bg, 0.0, 0.0, [single]($S - 1), [single]($S - 1))
    $bg.Dispose()

    $goldBrush = New-Object System.Drawing.SolidBrush($Gold)
    $whiteBrush = New-Object System.Drawing.SolidBrush($White)

    switch ($Style) {
        # 1 — gold frame, KAM large in white. Survives 16px because the word is the mark.
        1 {
            Draw-Brackets -G $g -U $u -Colour $Gold -Inset 50 -Arm 40 -Weight 16
            $font = New-Object System.Drawing.Font('Segoe UI', [single](60 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $whiteBrush -CenterX ([single](128 * $u)) -CenterY ([single](128 * $u)) -Tracking ([single](5 * $u))
            $font.Dispose()
        }
        # 2 — the original composition, restated in gold, with KAM along the foot.
        2 {
            Draw-Brackets -G $g -U $u -Colour $Gold -Inset 52 -Arm 34 -Weight 15
            Draw-Pencil -G $g -U $u -CX 128 -CY 118 -Scale 0.86
            $font = New-Object System.Drawing.Font('Segoe UI', [single](34 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $whiteBrush -CenterX ([single](128 * $u)) -CenterY ([single](203 * $u)) -Tracking ([single](7 * $u))
            $font.Dispose()
        }
        # 3 — monogram first: a gold K inside tight white crop corners.
        3 {
            Draw-Brackets -G $g -U $u -Colour $White -Inset 44 -Arm 30 -Weight 13
            $font = New-Object System.Drawing.Font('Segoe UI', [single](130 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'K' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](126 * $u)) -Tracking 0
            $font.Dispose()
        }
        # 4 — white frame, gold KAM, pencil tucked underneath as an accent.
        4 {
            Draw-Brackets -G $g -U $u -Colour $White -Inset 50 -Arm 38 -Weight 14
            $font = New-Object System.Drawing.Font('Segoe UI', [single](52 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](102 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
            Draw-Pencil -G $g -U $u -CX 128 -CY 172 -Scale 0.92 -Rotation 180
        }
    }

    $goldBrush.Dispose(); $whiteBrush.Dispose()
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

function Write-Ico {
    param([int]$Style, [string]$Path)

    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $frames = @()
    foreach ($s in $sizes) {
        $bmp = New-Mark -S $s -Style $Style
        $frames += [pscustomobject]@{ Size = $s; Bytes = (Get-PngBytes -Bitmap $bmp) }
        $bmp.Dispose()
    }

    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($f in $frames) {
        $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$f.Bytes.Length); $bw.Write([uint32]$offset)
        $offset += $f.Bytes.Length
    }
    foreach ($f in $frames) { $bw.Write($f.Bytes) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
}

if ($Sheet) {
    # A comparison sheet: each variant large, then at the sizes Windows uses.
    $cell = 320; $sheetW = $cell * 4; $sheetH = 470
    $canvas = New-Object System.Drawing.Bitmap($sheetW, $sheetH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics -Bitmap $canvas
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#EDEDEB'))

    $labelFont = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Bold)
    $noteFont = New-Object System.Drawing.Font('Segoe UI', 12)
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#16161A'))
    $dim = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#6A6A66'))
    $strip = New-Object System.Drawing.SolidBrush($Ink)

    $names = @(
        @('1  Gold frame, white KAM', 'the word is the mark'),
        @('2  Frame, pencil, KAM',    'closest to KAM Security'),
        @('3  Gold K monogram',       'boldest when small'),
        @('4  White frame, gold KAM', 'pencil as an accent')
    )

    for ($i = 0; $i -lt 4; $i++) {
        $x = $i * $cell
        $big = New-Mark -S 200 -Style ($i + 1)
        $g.DrawImage($big, [single]($x + ($cell - 200) / 2), 26.0, 200.0, 200.0)
        $big.Dispose()

        $g.FillRectangle($strip, [single]($x + 30), 250.0, [single]($cell - 60), 86.0)
        $sx = $x + 54
        foreach ($s in @(64, 48, 32, 16)) {
            $small = New-Mark -S $s -Style ($i + 1)
            $g.DrawImage($small, [single]$sx, [single](293 - $s / 2), [single]$s, [single]$s)
            $small.Dispose()
            $sx += $s + 22
        }

        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString($names[$i][0], $labelFont, $dark,
            (New-Object System.Drawing.RectangleF([single]$x, 356.0, [single]$cell, 30.0)), $fmt)
        $g.DrawString($names[$i][1], $noteFont, $dim,
            (New-Object System.Drawing.RectangleF([single]$x, 386.0, [single]$cell, 30.0)), $fmt)
        $g.DrawString('64   48   32   16 px', $noteFont, $dim,
            (New-Object System.Drawing.RectangleF([single]$x, 424.0, [single]$cell, 30.0)), $fmt)
    }

    $labelFont.Dispose(); $noteFont.Dispose(); $dark.Dispose(); $dim.Dispose(); $strip.Dispose()
    $g.Dispose()

    $sheetPath = Join-Path $root '..\assets\branding\icon-variations.png'
    $dir = Split-Path -Parent $sheetPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $canvas.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()

    # Also write each one as a usable .ico, so picking is just a rename.
    for ($i = 1; $i -le 4; $i++) {
        Write-Ico -Style $i -Path (Join-Path $root "..\assets\branding\kam-capture-v$i.ico")
    }

    Write-Host "Comparison sheet: $sheetPath"
    Write-Host "Individual icons: assets/branding/kam-capture-v1..4.ico"
    return
}

Write-Ico -Style $Variant -Path $OutIco

$png = New-Mark -S 256 -Style $Variant
$dir = Split-Path -Parent $OutPng
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$png.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

Write-Host "Icon written: $OutIco  (variant $Variant, $([math]::Round((Get-Item $OutIco).Length / 1kb, 1)) KB)"
Write-Host "PNG written:  $OutPng"
