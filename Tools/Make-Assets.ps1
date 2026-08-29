#Requires -Version 5.1
<#
.SYNOPSIS
    Renders the Evania product asset set into Assets\.

.DESCRIPTION
    Evania's mark is a four-point "AI sparkle" in a green -> blue gradient, floating on a deep
    slate Fluent tile with a soft two-hue glow behind it. Everything is vector geometry rendered by
    GDI+ at a super-sampled size and then down-sampled, so one set of constants produces both a
    16px taskbar plate and a 2480x1200 splash.

    Each output carries its own tile rather than shipping an alpha-only mark, because Windows
    plates some of these surfaces and not others and an alpha mark disappears on a light Start
    tile. Square tiles and the store logo stay text-free (Start overlays the app name over them);
    only the wide tile and the splash take the EVANIA wordmark.

    Sizes below 33px drop the secondary sparkle and the sheen -- at that area they smear into the
    main star, so the mark simplifies instead of shrinking.

    Emits the full WinUI/MSIX logo set (scales 100/125/150/200/400 + altform-unplated target
    sizes), Assets\Evania.ico, and a contact sheet under bin\ for visual review.

    To recolour or reshape: edit the tunables below and re-run.

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Make-Assets.ps1
                 [-OutDir <path>] [-SkipPreview]
#>
[CmdletBinding()]
param(
    [string]$OutDir,
    [switch]$SkipPreview
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutDir) {
    $OutDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets'))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ============================================================================
# Tunables
# ============================================================================

# Sparkle gradient, laid on a lower-left -> upper-right axis. The middle stop is a green-dominant
# aqua and the blue is an azure rather than a deep blue: on a near-black plate a dark blue reads
# as absence, and a blue-dominant midpoint makes the whole mark look blue with a green sliver.
$Green = 0x34E9A8
$Teal  = 0x12DFC0
$Blue  = 0x3B8CFF

# Tile and wordmark.
$TileHi  = 0x16203A
$TileMid = 0x0B1120
$TileLo  = 0x05080F
$Ink     = 0xEAF3FF
$InkTail = 0x9CC6FF

# Sparkle silhouette: how pinched the arms are, and tip sharpness in degrees.
$Waist = 0.235
$Flank = 22.0

# Wordmark.
$Wordmark     = 'EVANIA'
$FontFamily   = 'Segoe UI Semibold'
$FontFallback = 'Segoe UI'
$TrackingEm   = 0.17   # extra inter-glyph space, as a fraction of em
$CapRatio     = 0.72   # cap height as a fraction of em for this family

# Below this many physical pixels the mark renders in its simplified form.
$MiniBelow = 33

# ============================================================================
# Primitives
# ============================================================================

function New-C {
    param([int]$Hex, [int]$A = 255)
    [System.Drawing.Color]::FromArgb($A, ($Hex -shr 16) -band 0xFF, ($Hex -shr 8) -band 0xFF, $Hex -band 0xFF)
}

function New-Solid { param([int]$Hex, [int]$A = 255)
    New-Object System.Drawing.SolidBrush((New-C $Hex $A))
}

function Get-Pt {
    param([double]$AngleDeg, [double]$Radius, [double]$Cx, [double]$Cy)
    $t = [Math]::PI / 180.0 * $AngleDeg
    $x = $Cx + $Radius * [Math]::Cos($t)
    $y = $Cy + $Radius * [Math]::Sin($t)
    return @($x, $y)
}

function New-RoundedPath {
    param([double]$X, [double]$Y, [double]$W, [double]$H, [double]$R)
    $R = [Math]::Min($R, [Math]::Min($W, $H) / 2.0)
    if ($R -le 0) { $R = 0.0001 }
    $d = $R * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc([single]$X, [single]$Y, [single]$d, [single]$d, [single]180, [single]90)
    $p.AddArc([single]($X + $W - $d), [single]$Y, [single]$d, [single]$d, [single]270, [single]90)
    $p.AddArc([single]($X + $W - $d), [single]($Y + $H - $d), [single]$d, [single]$d, [single]0, [single]90)
    $p.AddArc([single]$X, [single]($Y + $H - $d), [single]$d, [single]$d, [single]90, [single]90)
    $p.CloseFigure()
    return $p
}

# Four cubic segments between tips at 0/90/180/270 degrees. The control points sit deep toward
# the centre, which is what pinches the sides concave and leaves sharp tips.
function New-SparklePath {
    param([double]$Cx, [double]$Cy, [double]$R)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.FillMode = [System.Drawing.Drawing2D.FillMode]::Winding
    for ($i = 0; $i -lt 4; $i++) {
        $ang = -90 + 90 * $i
        $tipA = Get-Pt $ang $R $Cx $Cy
        $tipB = Get-Pt ($ang + 90) $R $Cx $Cy
        $ctrl1 = Get-Pt ($ang + $Flank) ($R * $Waist) $Cx $Cy
        $ctrl2 = Get-Pt ($ang + 90 - $Flank) ($R * $Waist) $Cx $Cy
        if ($i -eq 0) { $p.StartFigure() }
        $p.AddBezier([single]$tipA[0], [single]$tipA[1], [single]$ctrl1[0], [single]$ctrl1[1],
                     [single]$ctrl2[0], [single]$ctrl2[1], [single]$tipB[0], [single]$tipB[1])
    }
    $p.CloseFigure()
    return $p
}

function New-SparkleBrush {
    param([double]$Cx, [double]$Cy, [double]$R)
    # The axis runs corner to corner of the mark's box, so the star's own pixels only ever cover
    # t = 0.25..0.75 of it. The blend therefore parks green and blue at the extremes with flat
    # ends beyond them: the full sweep lands on the arms, and the repeated ends mean WrapMode
    # (which GDI+ refuses to set to Clamp here) can never band the fill.
    $b1 = [System.Drawing.PointF]::new([single]($Cx - $R), [single]($Cy + $R))
    $b2 = [System.Drawing.PointF]::new([single]($Cx + $R), [single]($Cy - $R))
    $lb = New-Object System.Drawing.Drawing2D.LinearGradientBrush($b1, $b2, (New-C $Green), (New-C $Blue))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend
    $blend.Colors = [System.Drawing.Color[]]@(
        (New-C $Green), (New-C $Green), (New-C $Teal), (New-C $Blue), (New-C $Blue))
    $blend.Positions = [double[]]@(0.0, 0.22, 0.5, 0.78, 1.0)
    $lb.InterpolationColors = $blend
    return $lb
}

function New-DiagonalBrush {
    param([double]$X, [double]$Y, [double]$W, [double]$H, [int]$HexA, [int]$HexB)
    $p1 = [System.Drawing.PointF]::new([single]$X, [single]$Y)
    $p2 = [System.Drawing.PointF]::new([single]($X + $W), [single]($Y + $H))
    return New-Object System.Drawing.Drawing2D.LinearGradientBrush($p1, $p2, (New-C $HexA), (New-C $HexB))
}

function New-ThreeStopBrush {
    param([double]$X, [double]$Y, [double]$W, [double]$H, [int]$HexA, [int]$HexB, [int]$HexC)
    $lb = New-DiagonalBrush $X $Y $W $H $HexA $HexC
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend
    $blend.Colors = [System.Drawing.Color[]]@((New-C $HexA), (New-C $HexB), (New-C $HexC))
    $blend.Positions = [double[]]@(0.0, 0.55, 1.0)
    $lb.InterpolationColors = $blend
    return $lb
}

function Draw-Glow {
    param($G, [double]$Cx, [double]$Cy, [double]$R, [int]$Hex, [int]$Alpha)
    if ($Alpha -le 0 -or $R -le 0) { return }
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddEllipse([single]($Cx - $R), [single]($Cy - $R), [single]($R * 2), [single]($R * 2))
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($p)
    $pgb.CenterColor = New-C $Hex $Alpha
    $pgb.SurroundColors = [System.Drawing.Color[]]@(New-C $Hex 0)
    $G.FillEllipse($pgb, [single]($Cx - $R), [single]($Cy - $R), [single]($R * 2), [single]($R * 2))
    $pgb.Dispose(); $p.Dispose()
}

function Draw-TilePlate {
    param($G, [double]$X, [double]$Y, [double]$W, [double]$H, [double]$Radius,
          [double]$RimRatio = 0, [switch]$Sheen)

    $plate = New-RoundedPath $X $Y $W $H $Radius
    $fill = New-ThreeStopBrush $X $Y $W $H $TileHi $TileMid $TileLo
    $G.FillPath($fill, $plate)
    $fill.Dispose()

    # The sheen and the rim are clipped to the plate: GDI+ PenAlignment::Inset is only an
    # approximation on curves, so an unclipped rim leaves a pixel of coverage past the tile's own
    # anti-aliased edge.
    $save = $G.Save()
    $G.SetClip($plate)

    if ($Sheen) {
        Draw-Glow $G ($X + $W * 0.30) ($Y + $H * 0.14) ($W * 0.60) $Blue 20
    }

    if ($RimRatio -gt 0) {
        $rim = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            [System.Drawing.RectangleF]::new([single]$X, [single]$Y, [single]$W, [single]$H),
            (New-C 0xFFFFFF 28), (New-C 0xFFFFFF 5), [single]90)
        $pen = New-Object System.Drawing.Pen($rim, [single]([Math]::Max(0.6, $H * $RimRatio)))
        $pen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
        $G.DrawPath($pen, $plate)
        $pen.Dispose(); $rim.Dispose()
    }

    $G.Restore($save)
    $plate.Dispose()
}

function Draw-Sparkle {
    param($G, [double]$Cx, [double]$Cy, [double]$R, [switch]$Sheen)

    $path = New-SparklePath $Cx $Cy $R
    $brush = New-SparkleBrush $Cx $Cy $R
    $G.FillPath($brush, $path)
    $brush.Dispose()

    if ($Sheen) {
        # Vertical falloff across the mark's own box: bright crown, clear base.
        $sheenFill = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.RectangleF]::new([single]($Cx - $R), [single]($Cy - $R), [single]($R * 2), [single]($R * 2)),
            (New-C 0xFFFFFF 46), (New-C 0xFFFFFF 0), [single]90)
        $blend = New-Object System.Drawing.Drawing2D.ColorBlend
        $blend.Colors = [System.Drawing.Color[]]@(
            (New-C 0xFFFFFF 46), (New-C 0xFFFFFF 12), (New-C 0xFFFFFF 0), (New-C 0xFFFFFF 0))
        $blend.Positions = [double[]]@(0.0, 0.24, 0.75, 1.0)
        $sheenFill.InterpolationColors = $blend
        $G.FillPath($sheenFill, $path)
        $sheenFill.Dispose()
    }
    $path.Dispose()
}

<#
    Sparkle lockup for a square canvas of side $S at offset ($Ox, $Oy).
    $Full = 0..1000 design units of the plate, so every scale shares one geometry spec.
#>
function Draw-SquareMark {
    param($G, [double]$Ox, [double]$Oy, [double]$S, [switch]$Mini, [switch]$MaskOnly)

    $inset = if ($Mini) { 0.0 } else { 0.045 }
    $radius = if ($Mini) { 0.225 } else { 0.205 }
    $pad = $S * $inset
    $plateSide = $S - 2 * $pad
    $u = $plateSide / 1000.0
    $plateX = $Ox + $pad
    $plateY = $Oy + $pad

    if ($MaskOnly) {
        $mask = New-RoundedPath $plateX $plateY $plateSide $plateSide ($plateSide * $radius)
        $G.FillPath([System.Drawing.Brushes]::White, $mask)
        $mask.Dispose()
        return
    }

    Draw-TilePlate $G $plateX $plateY $plateSide $plateSide ($plateSide * $radius) `
        -RimRatio $(if ($Mini) { 0 } else { 0.0075 }) -Sheen

    # Everything above the plate is clipped to it: the glow's falloff otherwise floats a few
    # faint pixels outside the tile silhouette, which smudges on a light Start or splash backdrop.
    $clip = New-RoundedPath $plateX $plateY $plateSide $plateSide ($plateSide * $radius)
    $save = $G.Save()
    $G.SetClip($clip)

    $cx = $plateX + $plateSide / 2.0
    $cy = $plateY + $plateSide / 2.0

    if ($Mini) {
        $R = $u * 352
        Draw-Glow $G $cx $cy ($R * 1.75) $Teal 50
        Draw-Sparkle $G $cx $cy $R
    } else {
        $R = $u * 330
        $mcx = $cx - $u * 20
        $mcy = $cy + $u * 14
        Draw-Glow $G $mcx $mcy ($R * 1.95) $Teal 48
        Draw-Glow $G $mcx $mcy ($R * 1.05) $Green 42
        Draw-Sparkle $G $mcx $mcy $R -Sheen

        $sx = $cx + $u * 262
        $sy = $cy - $u * 262
        Draw-Glow $G $sx $sy ($R * 0.60) $Green 34
        Draw-Sparkle $G $sx $sy ($R * 0.355)
    }

    $G.Restore($save)
    $clip.Dispose()
}

<#
    Wordmark built as one filled path, letterspaced on ink width (optical tracking, which is what
    all-caps branding wants) and then scaled to fit its box, so no DPI scale can clip it.
    Returns a path already positioned in absolute canvas coordinates.
#>
function New-WordmarkPath {
    param([string]$Text, [double]$MaxW, [double]$CapH, [double]$X, [double]$Y)

    $probeEm = 100.0
    $track = [single]($probeEm * $TrackingEm)
    $fam = $null
    try { $fam = New-Object System.Drawing.FontFamily($FontFamily) } catch { $fam = $null }
    if (-not $fam -or $fam.Name -ne $FontFamily) {
        if ($fam) { $fam.Dispose() }
        $fam = New-Object System.Drawing.FontFamily($FontFallback)
    }
    $sf = New-Object System.Drawing.StringFormat
    $origin = [System.Drawing.PointF]::new(0, 0)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $penX = 0.0
    foreach ($ch in $Text.ToCharArray()) {
        $origin.X = [single]$penX
        $one = New-Object System.Drawing.Drawing2D.GraphicsPath
        $one.AddString([string]$ch, $fam, 0, [single]$probeEm, $origin, $sf)
        $glyphW = [double]$one.GetBounds().Width
        $path.AddPath($one, $false)
        $penX += $glyphW + $track
    }
    $sf.Dispose(); $fam.Dispose()

    $b = $path.GetBounds()
    $emCap = $probeEm * $CapRatio
    $scale = [Math]::Min(($MaxW / [double]$b.Width), ($CapH / $emCap))
    if ($scale -le 0 -or [double]::IsInfinity($scale) -or [double]::IsNaN($scale)) { $scale = 1.0 }

    $m = New-Object System.Drawing.Drawing2D.Matrix
    [void]$m.Scale([single]$scale, [single]$scale)
    $path.Transform($m)
    $b = $path.GetBounds()

    # Centre the fitted word inside the requested box, then move the box to ($X, $Y).
    $m.Reset()
    [void]$m.Translate(
        [single](($X + ($MaxW - [double]$b.Width) / 2.0) - $b.X),
        [single](($Y + ($CapH - [double]($b.Height * $scale)) / 2.0) - $b.Y))
    $path.Transform($m)
    $m.Dispose()

    $fit = $path.GetBounds()
    return [pscustomobject]@{
        Path      = $path
        Width     = [double]$fit.Width
        Height    = [double]$fit.Height
        Left      = [double]$fit.X
        Top       = [double]$fit.Y
    }
}

# ============================================================================
# Frames
# ============================================================================

function Draw-EvaniaFrame {
    param($G, [int]$W, [int]$H, [string]$Layout, [switch]$Mini, [switch]$MaskOnly)

    $G.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $G.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $G.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $G.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::GammaCorrected
    $G.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $G.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $G.Clear([System.Drawing.Color]::Transparent)

    switch ($Layout) {
        'Tile'   { Draw-SquareMark $G 0 0 $W -MaskOnly:$MaskOnly }
        'Plate'  { Draw-SquareMark $G 0 0 $W -Mini:$Mini -MaskOnly:$MaskOnly }
        'Wide'   { Draw-WideFrame $G $W $H -MaskOnly:$MaskOnly }
        'Splash' { Draw-SplashFrame $G $W $H -MaskOnly:$MaskOnly }
        default  { throw "unknown layout '$Layout'" }
    }
}

<#
    The horizontal brand lockup: plated mark at the left, the wordmark filling the rest of the
    plate. Shared by the Start wide tile (full bleed) and the splash (as a centered banner), so the
    wordmark never sits on bare transparency -- the splash backdrop follows the OS theme, and white
    letters over a light desktop would vanish.
#>
function Draw-Banner {
    param($G, [double]$X, [double]$Y, [double]$W, [double]$H, [switch]$MaskOnly)

    $pad = $H * 0.035
    $rad = $H * 0.19

    if ($MaskOnly) {
        $mask = New-RoundedPath ($X + $pad) ($Y + $pad) ($W - 2 * $pad) ($H - 2 * $pad) $rad
        $G.FillPath([System.Drawing.Brushes]::White, $mask)
        $mask.Dispose()
        return
    }

    Draw-TilePlate $G ($X + $pad) ($Y + $pad) ($W - 2 * $pad) ($H - 2 * $pad) $rad -RimRatio 0.01 -Sheen

    $clip = New-RoundedPath ($X + $pad) ($Y + $pad) ($W - 2 * $pad) ($H - 2 * $pad) $rad
    $save = $G.Save()
    $G.SetClip($clip)

    # Mark sized off the banner height so the lockup holds its proportions at every scale.
    $u = $H / 1000.0
    $mcx = $X + $W * 0.165
    $mcy = $Y + $H * 0.50
    $R = $H * 0.265
    Draw-Glow $G $mcx $mcy ($R * 1.95) $Teal 46
    Draw-Sparkle $G $mcx $mcy $R -Sheen
    Draw-Glow $G ($mcx + $u * 250) ($mcy - $u * 250) ($R * 0.60) $Green 36
    Draw-Sparkle $G ($mcx + $u * 250) ($mcy - $u * 250) ($R * 0.34)

    # The wordmark autofits the room that is left, so no DPI scale can push it off the plate.
    $boxX = $X + $W * 0.345
    $boxW = $X + $W * 0.930 - $boxX
    $boxH = $H * 0.30
    $wm = New-WordmarkPath $Wordmark $boxW $boxH $boxX ($mcy - $boxH / 2.0)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.RectangleF]::new([single]$boxX, [single]($mcy - $boxH / 2.0), [single]$boxW, [single]$boxH),
        (New-C $Ink), (New-C $InkTail), [single]0)
    $G.FillPath($brush, $wm.Path)
    $brush.Dispose(); $wm.Path.Dispose()

    $G.Restore($save)
    $clip.Dispose()
}

function Draw-WideFrame {
    param($G, [int]$W, [int]$H, [switch]$MaskOnly)
    Draw-Banner $G 0 0 $W $H -MaskOnly:$MaskOnly
}

function Draw-SplashFrame {
    param($G, [int]$W, [int]$H, [switch]$MaskOnly)
    $bh = $H * 0.46
    $bw = $bh * 2.5
    Draw-Banner $G (($W - $bw) / 2.0) (($H - $bh) / 2.0) $bw $bh -MaskOnly:$MaskOnly
}

# ============================================================================
# Rendering pipeline
# ============================================================================

function Get-Supersample {
    param([int]$W, [int]$H)
    $base = if ($W -le 64) { 4 } elseif ($W -le 200) { 3 } else { 2 }
    while ($base -gt 1 -and ($W * $base) * ($H * $base) -gt 12000000) { $base-- }
    return [Math]::Max(1, $base)
}

function Get-BitmapFromBytes {
    param([byte[]]$Bytes)
    $ms = [System.IO.MemoryStream]::new($Bytes)
    $bmp = New-Object System.Drawing.Bitmap($ms)
    return [pscustomobject]@{ Bitmap = $bmp; Stream = $ms }
}

<#
    Extracts the alpha plane of a PNG. Colour is useless for silhouette questions: at a soft edge a
    PNG's straight-alpha RGB is an amplification of premultiplied values, so it reads as noise while
    the alpha channel is exactly the coverage.
#>
function Get-AlphaPlane {
    param([byte[]]$Bytes)
    $holder = Get-BitmapFromBytes $Bytes
    $bmp = $holder.Bitmap
    $rect = [System.Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = [Math]::Abs($data.Stride)
    $buf = New-Object byte[] ($stride * $bmp.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)

    $plane = New-Object byte[] ($bmp.Width * $bmp.Height)
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        $row = $y * $stride
        $dst = $y * $bmp.Width
        for ($x = 0; $x -lt $bmp.Width; $x++) { $plane[$dst + $x] = $buf[$row + $x * 4 + 3] }
    }
    $bmp.Dispose(); $holder.Stream.Dispose()
    return , $plane
}

<#
    Asserts the silhouette: no asset pixel may be painted where the plate has no coverage. Catches
    paint escaping the tile (a glow falloff at a few percent alpha, invisible on Start but a smear
    on a light backdrop). The mask is rendered from the same geometry through the same pipeline, so
    it cannot drift from the art.

    Coverage is compared only where the mask is empty, never against the mask's value: GDI+ clips
    with hard edges while a path fill is anti-aliased, so paint clipped to the plate legitimately
    reaches fuller coverage than the fill alone inside the boundary ramp. That is still on the tile
    and is not a defect.
#>
function Get-SilhouetteStrays {
    param([byte[]]$Bytes, [int]$W, [int]$H, [string]$Layout, [bool]$Mini)
    $observed = Get-AlphaPlane $Bytes
    $plate = Get-AlphaPlane (Render-PngBytes -W $W -H $H -Layout $Layout -Mini:$Mini -MaskOnly)
    if ($observed.Length -ne $plate.Length) { return -1 }
    $count = 0
    for ($i = 0; $i -lt $observed.Length; $i++) {
        if ($plate[$i] -eq 0 -and $observed[$i] -gt 4) { $count++ }
    }
    return $count
}

function Render-PngBytes {
    param([int]$W, [int]$H, [string]$Layout, [switch]$Mini, [switch]$MaskOnly)

    $ss = Get-Supersample $W $H
    $mw = $W * $ss
    $mh = $H * $ss

    $master = New-Object System.Drawing.Bitmap($mw, $mh, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $mg = [System.Drawing.Graphics]::FromImage($master)
    Draw-EvaniaFrame -G $mg -W $mw -H $mh -Layout $Layout -Mini:$Mini -MaskOnly:$MaskOnly
    $mg.Dispose()

    $final = $master
    if ($ss -gt 1) {
        $final = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $fg = [System.Drawing.Graphics]::FromImage($final)
        $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $fg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        # TileFlipXY stops bicubic from sampling past the master's edge, which otherwise eats
        # alpha out of the outer row of full-bleed tiles.
        $ia = New-Object System.Drawing.Imaging.ImageAttributes
        [void]$ia.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $dst = [System.Drawing.Rectangle]::new(0, 0, $W, $H)
        [void]$fg.DrawImage($master, $dst, 0, 0, $mw, $mh, [System.Drawing.GraphicsUnit]::Pixel, $ia)
        $fg.Dispose(); $ia.Dispose(); $master.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    $final.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose(); $final.Dispose()
    return , $bytes
}

function Get-IcoBytes {
    param([int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256))

    # PNG-compressed entries: read by Vista and later, and far smaller than raw DIBs.
    $imgs = foreach ($s in ($Sizes | Sort-Object -Unique)) {
        [pscustomobject]@{ Size = $s; Bytes = (Render-PngBytes $s $s 'Plate' -Mini:($s -lt $MiniBelow)) }
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$imgs.Count)
    $offset = 6 + 16 * $imgs.Count
    foreach ($im in $imgs) {
        $dim = if ($im.Size -ge 256) { 0 } else { $im.Size }
        $bw.Write([byte]$dim)
        $bw.Write([byte]$dim)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$im.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $im.Bytes.Length
    }
    foreach ($im in $imgs) { $bw.Write($im.Bytes) }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

function Read-IcoEntries {
    param([byte[]]$Bytes)
    $count = [System.BitConverter]::ToUInt16($Bytes, 4)
    $entries = @()
    for ($i = 0; $i -lt $count; $i++) {
        $o = 6 + 16 * $i
        $w = $Bytes[$o]; if ($w -eq 0) { $w = 256 }
        $h = $Bytes[$o + 1]; if ($h -eq 0) { $h = 256 }
        $size = [System.BitConverter]::ToUInt32($Bytes, $o + 8)
        $off = [System.BitConverter]::ToUInt32($Bytes, $o + 12)
        $png = ($Bytes[$off] -eq 0x89 -and $Bytes[$off + 1] -eq 0x50 -and $Bytes[$off + 2] -eq 0x4E -and $Bytes[$off + 3] -eq 0x47)
        $entries += [pscustomobject]@{ W = $w; H = $h; Size = $size; Offset = $off; Png = $png }
    }
    return $entries
}

# ============================================================================
# Output manifest
# ============================================================================

$jobs = New-Object System.Collections.Generic.List[object]

function Add-Job {
    param([string]$Name, [int]$W, [int]$H, [string]$Layout)
    $jobs.Add([pscustomobject]@{ Name = $Name; W = $W; H = $H; Layout = $Layout })
}

# Midpoints must round away from zero: [Math]::Round's default banker's rounding turns 50 @125%
# into 62, where Windows documents 63.
function Px { param([double]$V) [int][Math]::Round($V, [System.MidpointRounding]::AwayFromZero) }

foreach ($s in @(100, 125, 150, 200, 400)) {
    $k = $s / 100.0
    Add-Job "Square44x44Logo.scale-$s.png"   (Px (44 * $k))  (Px (44 * $k))  'Tile'
    Add-Job "Square150x150Logo.scale-$s.png" (Px (150 * $k)) (Px (150 * $k)) 'Tile'
    Add-Job "StoreLogo.scale-$s.png"         (Px (50 * $k))  (Px (50 * $k))  'Tile'
    Add-Job "LockScreenLogo.scale-$s.png"    (Px (48 * $k))  (Px (48 * $k))  'Tile'
    Add-Job "Wide310x150Logo.scale-$s.png"   (Px (310 * $k)) (Px (150 * $k)) 'Wide'
    Add-Job "SplashScreen.scale-$s.png"      (Px (620 * $k)) (Px (300 * $k)) 'Splash'
}

# Package.appxmanifest's <Properties><Logo> names Assets\StoreLogo.png outright.
Add-Job 'StoreLogo.png' 50 50 'Tile'

foreach ($t in @(16, 24, 32, 48, 256)) {
    Add-Job "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t 'Plate'
}

# ============================================================================
# Render + write
# ============================================================================

Write-Host "Evania assets -> $OutDir" -ForegroundColor Cyan
$rendered = @{}
foreach ($j in $jobs) {
    $mini = ($j.Layout -eq 'Plate' -and $j.W -lt $MiniBelow)
    $bytes = Render-PngBytes -W $j.W -H $j.H -Layout $j.Layout -Mini:$mini
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir $j.Name), $bytes)
    $rendered[$j.Name] = $bytes
    Write-Host ("  {0,-58} {1,5}x{2}" -f $j.Name, $j.W, $j.H)
}

$ico = Get-IcoBytes
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'Evania.ico'), $ico)
Write-Host ("  {0,-58} {1,5}x{2}  {3} KB" -f 'Evania.ico', '', '', [int][Math]::Round($ico.Length / 1KB))

# ============================================================================
# Self-check: exact pixel size, alpha present, mark opaque where it must be
# ============================================================================

Write-Host ''
Write-Host 'Self-check' -ForegroundColor Cyan
$fail = New-Object System.Collections.Generic.List[string]
$expected = @{}
foreach ($j in $jobs) { $expected[$j.Name] = $j }

# Verify the verifier: paint one pixel where no plate exists and require the detector to see it. A
# guard that cannot fail is worse than no guard, because it reads as coverage.
$tamper = Get-BitmapFromBytes (Render-PngBytes 150 150 'Tile')
$tamper.Bitmap.SetPixel(0, 0, [System.Drawing.Color]::FromArgb(255, 255, 255, 255))
$probeMs = New-Object System.IO.MemoryStream
$tamper.Bitmap.Save($probeMs, [System.Drawing.Imaging.ImageFormat]::Png)
$probeBytes = $probeMs.ToArray()
$tamper.Bitmap.Dispose(); $tamper.Stream.Dispose(); $probeMs.Dispose()
$caught = Get-SilhouetteStrays $probeBytes 150 150 'Tile' $false
if ($caught -lt 1) {
    $fail.Add('silhouette detector missed deliberately injected paint - the check itself is broken')
} else {
    '  {0,-58} {1}' -f 'detector self-test (1 injected stray pixel)', "caught $caught"
}

foreach ($p in (Get-ChildItem -Path $OutDir -Filter '*.png' | Sort-Object Name)) {
    if (-not $expected.ContainsKey($p.Name)) {
        $fail.Add("$($p.Name): not produced by this script (stale asset?)")
        continue
    }
    $j = $expected[$p.Name]
    $holder = $null
    try {
        $raw = [System.IO.File]::ReadAllBytes($p.FullName)
        $holder = Get-BitmapFromBytes $raw
        $b = $holder.Bitmap
        if ($b.Width -ne $j.W -or $b.Height -ne $j.H) {
            $fail.Add("$($p.Name): $($b.Width)x$($b.Height), expected $($j.W)x$($j.H)")
        }
        if ($b.PixelFormat -ne [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) {
            $fail.Add("$($p.Name): pixel format $($b.PixelFormat), expected 32bppArgb")
        }
        # Corner must stay transparent (that is what makes the tile read as a tile); the middle of
        # a tile is always inside the mark or its plate, so it must be fully opaque.
        $corner = $b.GetPixel(0, 0)
        $center = $b.GetPixel([int]($b.Width / 2), [int]($b.Height / 2))
        if ($corner.A -gt 8) { $fail.Add("$($p.Name): corner not transparent (A=$($corner.A))") }
        if ($center.A -lt 250) { $fail.Add("$($p.Name): centre not opaque (A=$($center.A))") }
        $isMini = ($j.Layout -eq 'Plate' -and $j.W -lt $MiniBelow)
        $strays = Get-SilhouetteStrays $raw $b.Width $b.Height $j.Layout $isMini
        if ($strays -lt 0) {
            $fail.Add("$($p.Name): silhouette mask has a different size to the asset")
        } elseif ($strays -gt 0) {
            $fail.Add("$($p.Name): $strays pixel(s) painted outside the plate silhouette")
        }
        '{0,-58} {1,5}x{2,-4} corner A{3,-3} centre A{4,-3} outside plate {5}' -f `
            $p.Name, $b.Width, $b.Height, $corner.A, $center.A, $strays
    } catch {
        $fail.Add("$($p.Name): $($_.Exception.Message)")
    } finally {
        if ($holder) { $holder.Bitmap.Dispose(); $holder.Stream.Dispose() }
    }
}

$entries = Read-IcoEntries $ico
'  {0,-58} {1} entries: {2}' -f 'Evania.ico', $entries.Count, (($entries | ForEach-Object { $_.W }) -join '/')
if ($entries.Count -eq 0) { $fail.Add('Evania.ico: no entries') }
foreach ($e in $entries) {
    if (-not $e.Png) { $fail.Add("Evania.ico: $($e.W)x$($e.H) entry is not PNG-compressed") }
    if (($e.Offset + $e.Size) -gt $ico.Length) { $fail.Add("Evania.ico: $($e.W)x$($e.H) entry runs past EOF") }
}

# ============================================================================
# Contact sheet -- judging 16px legibility without deploying the package
# ============================================================================

if (-not $SkipPreview) {
    $sheetPath = Join-Path $OutDir '..\bin\AssetsPreview\evania-assets-preview.png'
    $sheetPath = [System.IO.Path]::GetFullPath($sheetPath)
    New-Item -ItemType Directory -Force -Path (Split-Path $sheetPath -Parent) | Out-Null

    $SW = 1520; $SH = 1260
    $sheet = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $sg = [System.Drawing.Graphics]::FromImage($sheet)
    $sg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $sg.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $sg.Clear([System.Drawing.Color]::White)

    $labelFont = New-Object System.Drawing.Font('Segoe UI', 8.5)
    $headFont = New-Object System.Drawing.Font('Segoe UI Semibold', 11)
    $grayBrush = New-Solid 0x5A6472
    $blackBrush = New-Solid 0x101418
    $whiteBrush = New-Solid 0xFFFFFF
    $slateBrush = New-Solid 0x1B1F27

    $checkerBmp = New-Object System.Drawing.Bitmap(24, 24, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $cbg = [System.Drawing.Graphics]::FromImage($checkerBmp)
    $cbg.Clear([System.Drawing.Color]::White)
    $cbg.FillRectangle((New-Solid 0xDDE3EC), 0, 0, 12, 12)
    $cbg.FillRectangle((New-Solid 0xDDE3EC), 12, 12, 12, 12)
    $cbg.Dispose()
    $checkerBrush = New-Object System.Drawing.TextureBrush($checkerBmp)

    function Add-Cell {
        param([int]$X, [int]$Y, [string]$Name, [double]$Scale = 1.0, [switch]$Checker, [int]$MaxW = 0)
        $bytes = $rendered[$Name]
        if (-not $bytes) { $fail.Add("preview: $Name was never rendered"); return $X }
        $holder = Get-BitmapFromBytes $bytes
        $img = $holder.Bitmap
        $w = [int]([Math]::Round($img.Width * $Scale))
        $h = [int]([Math]::Round($img.Height * $Scale))
        if ($MaxW -gt 0 -and $w -gt $MaxW) {
            $h = [int]([Math]::Round($h * ($MaxW / $w))); $w = $MaxW
        }
        if ($Checker) {
            $sg.FillRectangle($checkerBrush, [System.Drawing.Rectangle]::new(($X - 5), ($Y - 5), ($w + 10), ($h + 10)))
        }
        $sg.DrawImage($img, [System.Drawing.Rectangle]::new($X, $Y, $w, $h))
        $holder.Bitmap.Dispose(); $holder.Stream.Dispose()
        return [pscustomobject]@{ Next = $X + $w + 26; W = $w; H = $h }
    }

    # ---- A. light context, real pixels
    $sg.DrawString('Light context (real pixels, checker = transparent)', $headFont, $blackBrush, [single]24, [single]10)
    $x = 30
    foreach ($spec in @(
            @{ n = 'Square44x44Logo.scale-100.png'; t = '44x44' },
            @{ n = 'Square44x44Logo.scale-200.png'; t = '88 (44 @200%)' },
            @{ n = 'Square150x150Logo.scale-100.png'; t = '150x150' },
            @{ n = 'StoreLogo.png'; t = 'store 50' },
            @{ n = 'Square44x44Logo.targetsize-48_altform-unplated.png'; t = '48 unplated' },
            @{ n = 'Wide310x150Logo.scale-100.png'; t = 'wide 310x150' })) {
        $r = Add-Cell $x 52 $spec.n -Checker
        $sg.DrawString($spec.t, $labelFont, $grayBrush, [single]$x, [single]($r.H + 62))
        $x = $r.Next
    }

    # splash on the light sheet at half size (it renders ~that big on screen)
    $sg.DrawString('SplashScreen 620x300 at 0.5x', $labelFont, $grayBrush, [single]($x), [single]52)
    $r = Add-Cell $x 70 'SplashScreen.scale-100.png' -Scale 0.5 -Checker

    # ---- B. dark context
    $darkY = 452
    $sg.FillRectangle($slateBrush, [System.Drawing.Rectangle]::new(20, ($darkY - 26), ($SW - 40), 216))
    $sg.DrawString('Dark context (same files)', $headFont, $whiteBrush, [single]26, [single]($darkY - 22))
    $x = 30
    foreach ($n in @('Square44x44Logo.scale-100.png', 'Square150x150Logo.scale-100.png',
                     'Square44x44Logo.targetsize-48_altform-unplated.png',
                     'Square44x44Logo.targetsize-16_altform-unplated.png',
                     'StoreLogo.scale-400.png', 'Wide310x150Logo.scale-200.png',
                     'SplashScreen.scale-200.png')) {
        $r = Add-Cell $x ($darkY + 16) $n -MaxW 210
        $sg.DrawString(($n -replace '\.png$', ''), $labelFont, (New-Solid 0x8A93A0), [single]$x, [single]($darkY + 16 + $r.H + 4))
        $x = $r.Next + 8
    }

    # ---- C. legibility at small sizes, magnified nearest-neighbour
    $tinyY = 706
    $sg.DrawString('Small sizes magnified 5x (nearest neighbour) -- legibility check', $headFont, $blackBrush, [single]24, [single]($tinyY - 22))
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $x = 30
    foreach ($spec in @(
            @{ n = 'Square44x44Logo.targetsize-16_altform-unplated.png'; t = '16 unplated' },
            @{ n = 'Square44x44Logo.targetsize-24_altform-unplated.png'; t = '24 unplated' },
            @{ n = 'Square44x44Logo.targetsize-32_altform-unplated.png'; t = '32 unplated' },
            @{ n = 'Square44x44Logo.scale-100.png'; t = '44 tile' },
            @{ n = 'StoreLogo.png'; t = '50 store' })) {
        $r = Add-Cell $x ($tinyY + 14) $spec.n -Scale 5
        $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $sg.DrawString($spec.t, $labelFont, $grayBrush, [single]$x, [single]($tinyY + 14 + $r.H + 6))
        $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $x = $r.Next + 6
    }
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # ---- D. the .ico as Windows will read it
    $icoY = 1010
    $sg.DrawString('Assets\Evania.ico -- entries decoded back out of the file', $headFont, $blackBrush, [single]24, [single]($icoY - 22))
    $x = 30
    foreach ($e in (Read-IcoEntries $ico)) {
        if ($e.W -gt 128) { continue }
        $slice = New-Object byte[] $e.Size
        [Array]::Copy($ico, $e.Offset, $slice, 0, $e.Size)
        $holder = Get-BitmapFromBytes $slice
        $disp = if ($e.W -le 48) { $e.W * 2 } else { $e.W }
        $sg.FillRectangle($checkerBrush, [System.Drawing.Rectangle]::new(($x - 5), ($icoY + 14), ($disp + 10), ($disp + 10)))
        $sg.DrawImage($holder.Bitmap, [System.Drawing.Rectangle]::new($x, ($icoY + 14), $disp, $disp))
        $sg.DrawString("$($e.W)px", $labelFont, $grayBrush, [single]$x, [single]($icoY + 20 + $disp))
        $holder.Bitmap.Dispose(); $holder.Stream.Dispose()
        $x += $disp + 30
    }

    $sheet.Save($sheetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $sg.Dispose(); $sheet.Dispose(); $checkerBrush.Dispose(); $checkerBmp.Dispose()
    $grayBrush.Dispose(); $blackBrush.Dispose(); $whiteBrush.Dispose(); $slateBrush.Dispose()
    $labelFont.Dispose(); $headFont.Dispose()
    Write-Host ''
    Write-Host "Contact sheet: $sheetPath" -ForegroundColor Cyan
}

Write-Host ''
if ($fail.Count) {
    Write-Host 'SELF-CHECK FAILED' -ForegroundColor Red
    foreach ($f in $fail) { Write-Host "  $f" -ForegroundColor Red }
    exit 1
}
Write-Host ("PASS - {0} PNG + Evania.ico written, sizes/alpha/opacity verified." -f $jobs.Count) -ForegroundColor Green
exit 0
