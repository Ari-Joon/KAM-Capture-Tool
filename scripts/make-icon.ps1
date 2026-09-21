# Renders the KAM Capture Tool mark and packs a multi-resolution .ico.
#
# Variations on one idea, in the KAM black/white/gold palette the non-security
# tools use. Pure GDI+, so it needs nothing installed.
#
#   ./scripts/make-icon.ps1                 build the chosen variant
#   ./scripts/make-icon.ps1 -Variant 17     build a different one
#   ./scripts/make-icon.ps1 -Sheet          compare them all side by side
#   ./scripts/make-icon.ps1 -Sheet -Only 4,5,6
[CmdletBinding()]
param(
    [ValidateRange(1, 21)]
    [int]$Variant = 20,
    [switch]$Sheet,
    [int[]]$Only,
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

$Names = @{
    1 = @('Gold frame, white KAM',    'the word is the mark')
    2 = @('Frame, pencil, KAM',       'closest to KAM Security')
    3 = @('Gold K monogram',          'boldest when small')
    4 = @('Frame, KAM, small pencil', 'your pick, pencil 20% down')
    5 = @('Frame and KAM',            'variant 4 with the pencil gone')
    6 = @('Gold disc, black KAM',     'inverted, loudest in a taskbar')
    7 = @('Rounded tile',             'the Windows 11 app-tile shape')
    8 = @('Two corners, KAM',         'lightest frame, most open')
    9 = @('Lens ring, KAM',           'frame replaced by a rim')
    10 = @('Gold camera, KAM on it',  'wordmark across the body')
    11 = @('Camera, KAM in the lens', 'lens-forward')
    12 = @('White camera, gold lens', 'two-tone, KAM on the body')
    13 = @('Camera tile',             'Windows 11 app-tile shape')
    14 = @('Outlined camera',         'lighter weight, white KAM')
    15 = @('Gold disc, ink camera',   'inverted, loudest in a taskbar')
    16 = @('White body, gold ring',   'the original, ring cleared of the text')
    17 = @('Gold body, ink ring',     'warmest, highest contrast')
    18 = @('Gold body, white ring',   'ring reads first')
    19 = @('Ink body on gold',        'inverted, ring merges with the disc')
    20 = @('White body on a tile',    'the Windows 11 app-tile shape')
    21 = @('Mono body, gold flash',   'gold kept for one detail only')
}

function New-Graphics {
    param([System.Drawing.Bitmap]$Bitmap)
    $g = [System.Drawing.Graphics]::FromImage($Bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    return $g
}

# Letterspaced text centred on a point; GDI+ has no tracking of its own.
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
          [double]$Inset = 58, [double]$Arm = 38, [double]$Weight = 17,
          [string[]]$Corners = @('TL', 'TR', 'BL', 'BR'))

    $pen = New-Object System.Drawing.Pen($Colour, [single]($Weight * $U))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $i = $Inset * $U
    $a = $Arm * $U
    $f = (256 * $U) - $i

    if ($Corners -contains 'TL') {
        $G.DrawLine($pen, [single]$i, [single]($i + $a), [single]$i, [single]$i)
        $G.DrawLine($pen, [single]$i, [single]$i, [single]($i + $a), [single]$i)
    }
    if ($Corners -contains 'TR') {
        $G.DrawLine($pen, [single]($f - $a), [single]$i, [single]$f, [single]$i)
        $G.DrawLine($pen, [single]$f, [single]$i, [single]$f, [single]($i + $a))
    }
    if ($Corners -contains 'BL') {
        $G.DrawLine($pen, [single]$i, [single]($f - $a), [single]$i, [single]$f)
        $G.DrawLine($pen, [single]$i, [single]$f, [single]($i + $a), [single]$f)
    }
    if ($Corners -contains 'BR') {
        $G.DrawLine($pen, [single]($f - $a), [single]$f, [single]$f, [single]$f)
        $G.DrawLine($pen, [single]$f, [single]$f, [single]$f, [single]($f - $a))
    }
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

function New-RoundedPath {
    param([double]$X, [double]$Y, [double]$W, [double]$H, [double]$R)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single](2 * $R)
    $path.AddArc([single]$X, [single]$Y, $d, $d, 180, 90)
    $path.AddArc([single]($X + $W - $d), [single]$Y, $d, $d, 270, 90)
    $path.AddArc([single]($X + $W - $d), [single]($Y + $H - $d), $d, $d, 0, 90)
    $path.AddArc([single]$X, [single]($Y + $H - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}


# A compact camera: body, viewfinder bump, flash. Drawn in the 256 box so it
# scales with everything else.
function Draw-Camera {
    param([System.Drawing.Graphics]$G, [double]$U, [System.Drawing.Color]$Colour,
          [bool]$Outline = $false, [double]$Weight = 13)

    $body = New-RoundedPath -X (26 * $U) -Y (76 * $U) -W (204 * $U) -H (134 * $U) -R (24 * $U)
    $bump = New-RoundedPath -X (74 * $U) -Y (50 * $U) -W (60 * $U) -H (36 * $U) -R (10 * $U)

    $brush = New-Object System.Drawing.SolidBrush($Colour)

    if ($Outline) {
        # The bump stays solid so there is no seam where it meets the body.
        $G.FillPath($brush, $bump)
        $pen = New-Object System.Drawing.Pen($Colour, [single]($Weight * $U))
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $G.DrawPath($pen, $body)
        $pen.Dispose()
    }
    else {
        $G.FillPath($brush, $bump)
        $G.FillPath($brush, $body)
    }

    $brush.Dispose()
    $body.Dispose(); $bump.Dispose()
}

function Draw-Flash {
    param([System.Drawing.Graphics]$G, [double]$U, [System.Drawing.Color]$Colour)
    $brush = New-Object System.Drawing.SolidBrush($Colour)
    $G.FillEllipse($brush, [single](184 * $U), [single](56 * $U), [single](24 * $U), [single](24 * $U))
    $brush.Dispose()
}


# The lens-camera composition, with only the colours varying. The ring is sized
# from the text rather than guessed: at this weight "KAM" is about 2.24 em wide,
# and the chord of the ring at the cap height has to clear that with room to
# spare, or the stroke crowds the letterforms.
function Draw-LensCamera {
    param([System.Drawing.Graphics]$G, [double]$U,
          [System.Drawing.Color]$Body, [System.Drawing.Color]$Ring,
          [System.Drawing.Color]$Text, [System.Drawing.Color]$Flash)

    Draw-Camera -G $G -U $U -Colour $Body

    $flashBrush = New-Object System.Drawing.SolidBrush($Flash)
    $G.FillEllipse($flashBrush, [single](184 * $U), [single](56 * $U), [single](24 * $U), [single](24 * $U))
    $flashBrush.Dispose()

    $pen = New-Object System.Drawing.Pen($Ring, [single](13 * $U))
    $G.DrawEllipse($pen, [single](76 * $U), [single](91 * $U), [single](104 * $U), [single](104 * $U))
    $pen.Dispose()

    $brush = New-Object System.Drawing.SolidBrush($Text)
    $font = New-Object System.Drawing.Font('Segoe UI', [single](30 * $U), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    Draw-Tracked -G $G -Text 'KAM' -Font $font -Brush $brush -CenterX ([single](128 * $U)) -CenterY ([single](143 * $U)) -Tracking ([single](2 * $U))
    $font.Dispose(); $brush.Dispose()
}

function New-Mark {
    param([int]$S, [int]$Style)

    $u = $S / 256.0
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics -Bitmap $bmp
    $g.Clear([System.Drawing.Color]::Transparent)

    $goldBrush = New-Object System.Drawing.SolidBrush($Gold)
    $whiteBrush = New-Object System.Drawing.SolidBrush($White)
    $inkBrush = New-Object System.Drawing.SolidBrush($Ink)

    # A dark disc, for the family resemblance, unless the style says otherwise.
    if ($Style -eq 6 -or $Style -eq 15 -or $Style -eq 19) {
        $g.FillEllipse($goldBrush, 0.0, 0.0, [single]($S - 1), [single]($S - 1))
    }
    elseif ($Style -eq 7 -or $Style -eq 13 -or $Style -eq 20) {
        $tile = New-RoundedPath -X 0 -Y 0 -W ($S - 1) -H ($S - 1) -R (56 * $u)
        $g.FillPath($inkBrush, $tile)
        $tile.Dispose()
    }
    else {
        $g.FillEllipse($inkBrush, 0.0, 0.0, [single]($S - 1), [single]($S - 1))
    }

    switch ($Style) {
        1 {
            Draw-Brackets -G $g -U $u -Colour $Gold -Inset 50 -Arm 40 -Weight 16
            $font = New-Object System.Drawing.Font('Segoe UI', [single](60 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $whiteBrush -CenterX ([single](128 * $u)) -CenterY ([single](128 * $u)) -Tracking ([single](5 * $u))
            $font.Dispose()
        }
        2 {
            Draw-Brackets -G $g -U $u -Colour $Gold -Inset 52 -Arm 34 -Weight 15
            Draw-Pencil -G $g -U $u -CX 128 -CY 118 -Scale 0.86
            $font = New-Object System.Drawing.Font('Segoe UI', [single](34 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $whiteBrush -CenterX ([single](128 * $u)) -CenterY ([single](203 * $u)) -Tracking ([single](7 * $u))
            $font.Dispose()
        }
        3 {
            Draw-Brackets -G $g -U $u -Colour $White -Inset 44 -Arm 30 -Weight 13
            $font = New-Object System.Drawing.Font('Segoe UI', [single](130 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'K' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](126 * $u)) -Tracking 0
            $font.Dispose()
        }
        # 4 — the chosen composition, pencil down 20% from 0.92.
        4 {
            Draw-Brackets -G $g -U $u -Colour $White -Inset 50 -Arm 38 -Weight 14
            $font = New-Object System.Drawing.Font('Segoe UI', [single](52 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](104 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
            Draw-Pencil -G $g -U $u -CX 128 -CY 170 -Scale 0.74 -Rotation 180
        }
        # 5 — the same, with nothing competing for the middle.
        5 {
            Draw-Brackets -G $g -U $u -Colour $White -Inset 50 -Arm 40 -Weight 15
            $font = New-Object System.Drawing.Font('Segoe UI', [single](62 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](128 * $u)) -Tracking ([single](5 * $u))
            $font.Dispose()
        }
        # 6 — inverted. No frame at all; the disc itself carries the colour.
        6 {
            $font = New-Object System.Drawing.Font('Segoe UI', [single](68 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $inkBrush -CenterX ([single](128 * $u)) -CenterY ([single](128 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
        }
        # 7 — squared off, the way Windows 11 draws most app tiles.
        7 {
            Draw-Brackets -G $g -U $u -Colour $Gold -Inset 56 -Arm 36 -Weight 15
            $font = New-Object System.Drawing.Font('Segoe UI', [single](56 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $whiteBrush -CenterX ([single](128 * $u)) -CenterY ([single](128 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
        }
        # 8 — two corners instead of four: still a crop, half the furniture.
        8 {
            Draw-Brackets -G $g -U $u -Colour $White -Inset 46 -Arm 44 -Weight 15 -Corners @('TL', 'BR')
            $font = New-Object System.Drawing.Font('Segoe UI', [single](58 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](128 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
        }
        # 9 — a rim rather than brackets: reads as a lens, keeps the wordmark.
        9 {
            $pen = New-Object System.Drawing.Pen($Gold, [single](13 * $u))
            $g.DrawEllipse($pen, [single](26 * $u), [single](26 * $u), [single](204 * $u), [single](204 * $u))
            $pen.Dispose()
            $font = New-Object System.Drawing.Font('Segoe UI', [single](62 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $whiteBrush -CenterX ([single](128 * $u)) -CenterY ([single](128 * $u)) -Tracking ([single](5 * $u))
            $font.Dispose()
        }
        # 10 — the wordmark rides on the camera body.
        10 {
            Draw-Camera -G $g -U $u -Colour $Gold
            Draw-Flash -G $g -U $u -Colour $Ink
            $font = New-Object System.Drawing.Font('Segoe UI', [single](56 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $inkBrush -CenterX ([single](128 * $u)) -CenterY ([single](144 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
        }
        # 11 — lens first: KAM sits where the glass would be.
        11 {
            Draw-Camera -G $g -U $u -Colour $Gold
            Draw-Flash -G $g -U $u -Colour $Ink
            $g.FillEllipse($inkBrush, [single](80 * $u), [single](96 * $u), [single](96 * $u), [single](96 * $u))
            $font = New-Object System.Drawing.Font('Segoe UI', [single](32 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](144 * $u)) -Tracking ([single](2 * $u))
            $font.Dispose()
        }
        # 12 — two-tone: white body, gold lens ring, KAM cut out of the body.
        12 { Draw-LensCamera -G $g -U $u -Body $White -Ring $Gold -Text $Ink -Flash $Gold }
        # 13 — the same camera on the tile shape Windows 11 prefers.
        13 {
            Draw-Camera -G $g -U $u -Colour $Gold
            Draw-Flash -G $g -U $u -Colour $Ink
            $font = New-Object System.Drawing.Font('Segoe UI', [single](56 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $inkBrush -CenterX ([single](128 * $u)) -CenterY ([single](144 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
        }
        # 14 — drawn rather than filled, so the disc shows through.
        14 {
            Draw-Camera -G $g -U $u -Colour $Gold -Outline $true -Weight 13
            $font = New-Object System.Drawing.Font('Segoe UI', [single](54 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $whiteBrush -CenterX ([single](128 * $u)) -CenterY ([single](146 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
        }
        # 15 — inverted: a black camera stamped on a gold coin.
        15 {
            Draw-Camera -G $g -U $u -Colour $Ink
            Draw-Flash -G $g -U $u -Colour $Gold
            $font = New-Object System.Drawing.Font('Segoe UI', [single](56 * $u), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            Draw-Tracked -G $g -Text 'KAM' -Font $font -Brush $goldBrush -CenterX ([single](128 * $u)) -CenterY ([single](144 * $u)) -Tracking ([single](4 * $u))
            $font.Dispose()
        }
        # ---- colourways of the same lens-camera composition ----
        16 { Draw-LensCamera -G $g -U $u -Body $White -Ring $Gold  -Text $Ink   -Flash $Gold }
        17 { Draw-LensCamera -G $g -U $u -Body $Gold  -Ring $Ink   -Text $Ink   -Flash $Ink }
        18 { Draw-LensCamera -G $g -U $u -Body $Gold  -Ring $White -Text $Ink   -Flash $White }
        19 { Draw-LensCamera -G $g -U $u -Body $Ink   -Ring $Gold  -Text $Gold  -Flash $Gold }
        20 { Draw-LensCamera -G $g -U $u -Body $White -Ring $Gold  -Text $Ink   -Flash $Gold }
        21 { Draw-LensCamera -G $g -U $u -Body $White -Ring $Ink   -Text $Ink   -Flash $Gold }
    }

    $goldBrush.Dispose(); $whiteBrush.Dispose(); $inkBrush.Dispose()
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
    $list = if ($Only) { $Only } else { 1..21 }
    $cols = [math]::Min(3, $list.Count)
    $rows = [math]::Ceiling($list.Count / $cols)

    $cell = 340; $cellH = 470
    $sheetW = $cell * $cols; $sheetH = [int]($cellH * $rows)

    $canvas = New-Object System.Drawing.Bitmap($sheetW, $sheetH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics -Bitmap $canvas
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#EDEDEB'))

    $labelFont = New-Object System.Drawing.Font('Segoe UI', 15, [System.Drawing.FontStyle]::Bold)
    $noteFont = New-Object System.Drawing.Font('Segoe UI', 12)
    $dark = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#16161A'))
    $dim = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#6A6A66'))
    $strip = New-Object System.Drawing.SolidBrush($Ink)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center

    for ($n = 0; $n -lt $list.Count; $n++) {
        $style = $list[$n]
        $x = ($n % $cols) * $cell
        $y = [math]::Floor($n / $cols) * $cellH

        $big = New-Mark -S 200 -Style $style
        $g.DrawImage($big, [single]($x + ($cell - 200) / 2), [single]($y + 26), 200.0, 200.0)
        $big.Dispose()

        $g.FillRectangle($strip, [single]($x + 34), [single]($y + 250), [single]($cell - 68), 86.0)
        $sx = $x + 62
        foreach ($s in @(64, 48, 32, 16)) {
            $small = New-Mark -S $s -Style $style
            $g.DrawImage($small, [single]$sx, [single]($y + 293 - $s / 2), [single]$s, [single]$s)
            $small.Dispose()
            $sx += $s + 22
        }

        $title = "$style  " + $Names[$style][0]
        $g.DrawString($title, $labelFont, $dark,
            (New-Object System.Drawing.RectangleF([single]$x, [single]($y + 356), [single]$cell, 30.0)), $fmt)
        $g.DrawString($Names[$style][1], $noteFont, $dim,
            (New-Object System.Drawing.RectangleF([single]$x, [single]($y + 386), [single]$cell, 30.0)), $fmt)
        $g.DrawString('64   48   32   16 px', $noteFont, $dim,
            (New-Object System.Drawing.RectangleF([single]$x, [single]($y + 424), [single]$cell, 30.0)), $fmt)
    }

    $labelFont.Dispose(); $noteFont.Dispose(); $dark.Dispose(); $dim.Dispose(); $strip.Dispose()
    $g.Dispose()

    $sheetPath = Join-Path $root '..\assets\branding\icon-variations.png'
    $dir = Split-Path -Parent $sheetPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $canvas.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()

    foreach ($style in $list) {
        Write-Ico -Style $style -Path (Join-Path $root "..\assets\branding\kam-capture-v$style.ico")
    }

    Write-Host "Comparison sheet: $sheetPath"
    Write-Host ("Individual icons written for: {0}" -f ($list -join ', '))
    return
}

Write-Ico -Style $Variant -Path $OutIco

$png = New-Mark -S 256 -Style $Variant
$dir = Split-Path -Parent $OutPng
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$png.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)

$asset = Join-Path $root '..\src\KamCapture\Assets\kam-capture.png'
$assetDir = Split-Path -Parent $asset
if (-not (Test-Path $assetDir)) { New-Item -ItemType Directory -Path $assetDir -Force | Out-Null }
$png.Save($asset, [System.Drawing.Imaging.ImageFormat]::Png)
$png.Dispose()

Write-Host "Icon written: $OutIco  (variant $Variant, $([math]::Round((Get-Item $OutIco).Length / 1kb, 1)) KB)"
Write-Host "PNG written:  $OutPng"
Write-Host "App asset:    $asset"
