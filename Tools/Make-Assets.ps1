#Requires -Version 5.1
<#
.SYNOPSIS
    Renders the Evania product asset set into Assets\.

.DESCRIPTION
    Evania's mark is the officer: a figure in a light-blue uniform under a cap carrying a gold star,
    on a deep slate Fluent tile with a soft glow behind it. The geometry is traced from the authored
    drawing in Tools\Mascot, so the silhouette in a 16px taskbar plate and in a 2480x1200 splash is
    literally the same outline, and it is rendered by GDI+ at a super-sampled size then down-sampled.

    Tracing rather than re-typing the shapes is also what makes the recolour possible: the drawing
    is a flat two-tone image whose parts are separable as regions, so every stroke becomes uniform
    blue, every enclosed opening becomes skin, and the star knocked out of the cap becomes gold. The
    glasses are lifted out and the eyes are drawn from scratch in the space they occupied.

    Each output carries its own tile rather than shipping an alpha-only mark, because Windows
    plates some of these surfaces and not others and an alpha mark disappears on a light Start
    tile. Square tiles and the store logo stay text-free (Start overlays the app name over them);
    only the wide tile and the splash take the EVANIA wordmark.

    Sizes below 33px crop to the cap and its star -- a face cannot survive 16px, but the star is
    already a clean five-point shape, so the mark simplifies by zooming rather than by shrinking.

    Emits the full WinUI/MSIX logo set (scales 100/125/150/200/400 + altform-unplated target
    sizes), Assets\Evania.ico, and a contact sheet under bin\ for visual review.

    To recolour or reshape: edit the tunables below and re-run. To replace the character outright,
    drop a new flat drawing at $MascotSource; the zone split is documented in Initialize-Mascot.

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
Add-Type -Path (Join-Path $PSScriptRoot 'EvaniaMascot.cs') -ReferencedAssemblies @('System.Drawing')

if (-not $OutDir) {
    $OutDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets'))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ============================================================================
# Tunables
# ============================================================================

# The mark is traced from the authored artwork in Tools\Mascot rather than re-entered by hand, so
# the silhouette in a 16px plate and in a 2480px splash is one geometry. Recolour it by zone: every
# stroke and solid fill of the drawing becomes the uniform, every enclosed opening becomes skin, and
# the star knocked out of the cap becomes gold. Point $MascotSource at another drawing and the
# whole set follows it.
$MascotSource = Join-Path $PSScriptRoot 'Mascot\mascot-source.png'

# Uniform: one ramp top to bottom. Cap, hair and jacket are a single connected mass in the drawing,
# so splitting them would mean inventing cuts through it. Light at the crown reads as lit, deep at
# the shoulders holds the tile.
$ClothHi  = 0x9BD8FF
$ClothMid = 0x3D86E8
$ClothLo  = 0x16418F

# Skin, blush, and the gold of the cap star.
$SkinHi  = 0xF9DCC0
$SkinLo  = 0xDDA985
$Blush   = 0xE8756E
$GoldHi  = 0xFFEFB0
$GoldMid = 0xF2C24A
$GoldLo  = 0xB97C15

# Eyes. The drawing's are a black disc with a white dot behind thick round rims; the rims come off
# and these go in their place. Names end in Ink/Surface deliberately: PowerShell variables are
# case-insensitive, so a tunable called $Pupil is the same variable as any local $pupil -- and the
# eye code needs several of those.
$IrisHi    = 0xBFEFFF
$IrisMid   = 0x3E8FD8
$IrisLo    = 0x14507F
$PupilInk  = 0x0D1522
$LashInk   = 0x1B2740
$ScleraSurf = 0xF7FBFF

# Tile and wordmark.
$TileHi  = 0x16203A
$TileMid = 0x0B1120
$TileLo  = 0x05080F
$Ink     = 0xEAF3FF
$InkTail = 0x9CC6FF
$GlowCool  = 0x3B8CFF
$GlowWarm  = 0xFFC65E

# Source-space rectangles, in the drawing's own pixels. Which one is mapped into the destination
# square is the only difference between the full head and the tiny-plate crop, so both stay in
# agreement without a second file to keep in step.
$BoxHead = [pscustomobject]@{ X = 58.0;  Y = 39.0; W = 220.0; H = 256.0 }    # whole figure
$BoxStar = [pscustomobject]@{ X = 104.0; Y = 39.0; W = 127.0; H = 127.0 }    # cap and star
$StarBox = [pscustomobject]@{ X = 140;   Y = 59;   W = 56;    H = 56 }       # the star's opening
$EyeAt   = [pscustomobject]@{ X = 137.0; Y = 169.0 }, [pscustomobject]@{ X = 198.0; Y = 169.0 }
$EyeRx   = 28.0     # the rim reached r=28 on its widest ray; no hair within 1px of that
$EyeRy   = 25.0

# Bevel. Heights are how far a zone stands proud of the surface, in the same units as the base.
$BevelStrength = 0.62
$BevelSpecular = 0.34
$BevelSoftness = 0.026     # blur radius as a fraction of the mark's size
$ClothLift     = 0.30
$StarLift      = 0.62
$SocketDip     = -0.26
$ShadowAlpha   = 96
$ShadowDrop    = 0.055     # offset as a fraction of the mark's size

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

<#
    Traces the source drawing once into three source-space paths -- cloth (every stroke and solid
    fill), skin (every opening), star (the opening inside the cap) -- plus the untouched silhouette
    for the drop shadow.

    The glasses are lifted before tracing: the rims, the bridge and the pupils are removed with two
    ellipses sized to the measured rim, and the fragments that stay welded to the hair are cleared
    as islands. ClearStrays demands that a leftover be cut off from the window's border, and the
    hair flows through that window, so the rule cannot eat the hair.
#>
function Initialize-Mascot {
    param([string]$Path, [object]$StarBox, [object[]]$Eyes, [double]$Rx, [double]$Ry)

    if (-not (Test-Path -LiteralPath $Path)) { throw "mascot source not found: $Path" }
    $art = [EvaniaMascot]::Load($Path)
    $w = $art.W
    $h = $art.H

    $ink = [EvaniaMascot]::Ink($art, 128)
    $figure = [EvaniaMascot]::Figure($art)

    $glasses = [EvaniaMascot]::Union(
        [EvaniaMascot]::Ellipse($w, $h, $Eyes[0].X, $Eyes[0].Y, $Rx, $Ry),
        [EvaniaMascot]::Ellipse($w, $h, $Eyes[1].X, $Eyes[1].Y, $Rx, $Ry),
        [EvaniaMascot]::Box($w, $h, 160, 148, 175, 190))
    $cloth = [EvaniaMascot]::Subtract($ink, $glasses)
    [void][EvaniaMascot]::ClearStrays($cloth, $w, $h, 96, 134, 240, 205, 220)

    $open = [EvaniaMascot]::Subtract($figure, $cloth)
    $starWin = [EvaniaMascot]::Box($w, $h, [int]$StarBox.X, [int]$StarBox.Y,
                                   [int]($StarBox.X + $StarBox.W), [int]($StarBox.Y + $StarBox.H))
    # a AND b as a Subtract(a, Subtract(a, b)): the star is the opening that falls inside the cap.
    $star = [EvaniaMascot]::Subtract($open, [EvaniaMascot]::Subtract($open, $starWin))
    $skin = [EvaniaMascot]::Subtract($open, $star)

    return [pscustomobject]@{
        W      = $w
        H      = $h
        Cloth  = New-PathFromLoops ([EvaniaMascot]::Trace($cloth, $w, $h, 0.5, 3))
        Skin   = New-PathFromLoops ([EvaniaMascot]::Trace($skin, $w, $h, 0.5, 3))
        Star   = New-PathFromLoops ([EvaniaMascot]::Trace($star, $w, $h, 0.35, 2))
        Figure = New-PathFromLoops ([EvaniaMascot]::Trace($figure, $w, $h, 0.5, 3))
        # Kept so the self-check can rasterise each path again and hold it against the mask it was
        # traced from: that is the only way to know the vector still says what the drawing says.
        ClothMask  = $cloth
        SkinMask   = $skin
        StarMask   = $star
        FigureMask = $figure
        ClothPx  = [EvaniaMascot]::Area($cloth)
        SkinPx   = [EvaniaMascot]::Area($skin)
        StarPx   = [EvaniaMascot]::Area($star)
        FigurePx = [EvaniaMascot]::Area($figure)
    }
}

function New-PathFromLoops {
    param([System.Collections.Generic.List[double[]]]$Loops)
    # One path, even-odd. The cloth has an opening that carries an island that carries a further
    # opening, and parity resolves every level without anyone tracking which contour is inside which.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
    foreach ($lp in $Loops) {
        $pts = New-Object System.Drawing.PointF[] ($lp.Length / 2)
        for ($k = 0; $k -lt $pts.Length; $k++) {
            $pts[$k] = New-Object System.Drawing.PointF ([single]$lp[$k * 2], [single]$lp[$k * 2 + 1])
        }
        $path.AddPolygon([System.Drawing.PointF[]]$pts)
    }
    return $path
}

# Uniform scale, centred on the destination square, so the drawing keeps its proportions whatever
# it is dropped into.
function New-SrcToDst {
    param([object]$Box, [double]$DstX, [double]$DstY, [double]$DstSide)
    $s = [Math]::Min($DstSide / $Box.W, $DstSide / $Box.H)
    $m = New-Object System.Drawing.Drawing2D.Matrix
    [void]$m.Translate([single]($DstX + ($DstSide - $Box.W * $s) / 2.0),
                       [single]($DstY + ($DstSide - $Box.H * $s) / 2.0))
    [void]$m.Scale([single]$s, [single]$s)
    [void]$m.Translate([single](-$Box.X), [single](-$Box.Y))
    return [pscustomobject]@{ Matrix = $m; Scale = $s }
}

<#
    The eye, authored in local coordinates with its centre at the origin and its outer corner
    towards +x. Drawing it twice -- once straight, once mirrored about the face's vertical axis --
    is what makes the pair exactly symmetrical, which the source's own eyes were not.

    Corner out and up, iris breaking the upper lid, lashes thickening towards the temple: that is
    the "alluring" brief expressed as geometry rather than as a bit-map of the reference.
#>
function New-EyeShape {
    $scleraPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $scleraPath.StartFigure()
    $scleraPath.AddBezier([single]-21, [single]2.5, [single]-13, [single]-12.5,
                          [single]6, [single]-15.0, [single]21, [single]-4.0)
    $scleraPath.AddBezier([single]21, [single]-4.0, [single]9, [single]7.5,
                          [single]-6, [single]10.5, [single]-21, [single]2.5)
    $scleraPath.CloseFigure()

    $lidPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $lidPath.StartFigure()
    $lidPath.AddBezier([single]-21.5, [single]2.0, [single]-13, [single]-13.5,
                       [single]6, [single]-16.2, [single]21.5, [single]-4.6)
    $lidPath.AddBezier([single]21.5, [single]-4.6, [single]8, [single]5.5,
                       [single]-6, [single]8.6, [single]-21.5, [single]2.0)
    $lidPath.CloseFigure()

    # Lash band: the lid lifted by a taper, so it is thin at the nose and heavy at the temple.
    $lashPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $lashPath.StartFigure()
    $lashPath.AddBezier([single]-21.5, [single]2.0, [single]-13, [single]-13.5,
                        [single]6, [single]-16.2, [single]21.5, [single]-4.6)
    $lashPath.AddLine([single]21.5, [single]-4.6, [single]27.5, [single]-10.5)    # the wing
    $lashPath.AddBezier([single]27.5, [single]-10.5, [single]16, [single]-13.5,
                        [single]4, [single]-20.6, [single]-12.5, [single]-15.0)
    $lashPath.AddBezier([single]-12.5, [single]-15.0, [single]-17, [single]-11.5,
                        [single]-20, [single]-6.0, [single]-21.5, [single]2.0)
    $lashPath.CloseFigure()

    $browPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $browPath.StartFigure()
    $browPath.AddBezier([single]-20.5, [single]-22.5, [single]-9, [single]-30.5,
                        [single]9, [single]-31.0, [single]24.0, [single]-22.0)
    $browPath.AddBezier([single]24.0, [single]-22.0, [single]10, [single]-26.0,
                        [single]-8, [single]-25.0, [single]-20.5, [single]-18.0)
    $browPath.CloseFigure()

    return [pscustomobject]@{
        Sclera = $scleraPath
        Lid    = $lidPath
        Lash   = $lashPath
        Brow   = $browPath
        Iris   = [pscustomobject]@{ X = 1.0;  Y = -2.0; R = 9.4 }
        Pupil  = [pscustomobject]@{ X = 1.0;  Y = -2.0; R = 4.5 }
        Key    = [pscustomobject]@{ X = -3.6; Y = -7.2; R = 3.1 }
        Fill   = [pscustomobject]@{ X = 5.2;  Y = 3.0;  R = 1.5 }
        # Three detached lashes past the outer corner, shortest nearest the wing.
        Flutters = @(
            [pscustomobject]@{ P1 = @(22.0, -6.0);  C = @(26.5, -10.5); P2 = @(29.0, -14.0); W = 1.7 },
            [pscustomobject]@{ P1 = @(17.5, -10.5); C = @(21.0, -15.5); P2 = @(22.0, -19.0); W = 1.4 },
            [pscustomobject]@{ P1 = @(11.0, -13.5); C = @(13.5, -19.0); P2 = @(13.8, -22.5); W = 1.2 })
    }
}


# A vertical ramp in source coordinates. Every zone is lit the same way -- bright at the crown,
# deep at the base -- so the character reads as one object under one light rather than as a
# patchwork of separately shaded parts.
function New-ZoneBrush {
    param([double]$Top, [double]$Bottom, [int]$HexA, [int]$HexB, [int]$HexC, [double]$Mid)
    $lb = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.RectangleF]::new([single]0, [single]$Top, [single]1, [single][Math]::Max(1.0, $Bottom - $Top)),
        (New-C $HexA), (New-C $HexC), [single]90)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend
    $blend.Colors = [System.Drawing.Color[]]@((New-C $HexA), (New-C $HexB), (New-C $HexC))
    $blend.Positions = [double[]]@(0.0, $Mid, 1.0)
    $lb.InterpolationColors = $blend
    return $lb
}

function New-ReliefBrush {
    param([double]$Level)
    # Relief is stored as an 8-bit height: 0 is the page, 128 is the surface the mark sits on, and
    # each zone's lift is measured from that. Reading it back as a float and blurring it is what
    # turns the flat fills into walls the bevel can catch light on.
    $v = [int][Math]::Max(0, [Math]::Min(255, [Math]::Round(128 + $Level * 127, [System.MidpointRounding]::AwayFromZero)))
    New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, $v, $v, $v))
}

function New-RadialBrush {
    param($Path, [int]$Inner, [int]$Outer, [int]$AIn, [int]$AOut)
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($Path)
    $pgb.CenterColor = New-C $Inner $AIn
    $pgb.SurroundColors = [System.Drawing.Color[]]@(New-C $Outer $AOut)
    return $pgb
}

<#
    One eye, drawn in the local space New-EyeShape authored: centre at the origin, outer corner
    towards +x. The caller mirrors it for the other side, which is the only way to guarantee the
    pair matches -- the source drawing's two eyes were round blobs, so nothing in it constrains
    symmetry now that they are replaced.

    Everything is stroked in local units and let the page transform scale it, so a lash keeps its
    weight relative to the eye at 16px and at 1500px.
#>
function Draw-MascotEye {
    param($G, [object]$Eye, [object]$Shape)

    $lashPen = New-Object System.Drawing.Pen((New-C $LashInk 255), [single]1.0)
    $lashPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $lashPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $lashPen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Center

    # The socket: a soft warm shadow so the eye sits in the face instead of on top of it.
    $socketPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $socketPath.AddEllipse([single]-27.0, [single]-21.0, [single]54.0, [single]34.0)
    $sb = New-RadialBrush $socketPath 0x9A5A3C 0x9A5A3C 62 0
    $G.FillPath($sb, $socketPath)
    $sb.Dispose(); $socketPath.Dispose()

    # Sclera.
    $scleraFill = New-Solid $ScleraSurf
    $G.FillPath($scleraFill, $Shape.Sclera)
    $scleraFill.Dispose()

    $save = $G.Save()
    $G.SetClip($Shape.Sclera)

    # Iris: dark at the limbal ring, brighter around the pupil, which is what keeps a small iris
    # from reading as a flat dot.
    $irisPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $irisPath.AddEllipse([single]($Shape.Iris.X - $Shape.Iris.R), [single]($Shape.Iris.Y - $Shape.Iris.R),
                         [single]($Shape.Iris.R * 2), [single]($Shape.Iris.R * 2))
    $ib = New-RadialBrush $irisPath $IrisHi $IrisLo 255 255
    $G.FillPath($ib, $irisPath)
    $ib.Dispose()

    $limbal = New-Object System.Drawing.Pen((New-C $IrisLo 215), [single]1.5)
    $G.DrawPath($limbal, $irisPath)
    $limbal.Dispose()

    # Bounce light along the iris's lower edge: the only thing that makes a drawn eye look wet.
    $bounce = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bounce.AddEllipse([single]($Shape.Iris.X - $Shape.Iris.R * 0.9), [single]($Shape.Iris.Y - $Shape.Iris.R * 0.2),
                       [single]($Shape.Iris.R * 1.8), [single]($Shape.Iris.R * 1.5))
    $gb = New-RadialBrush $bounce $IrisMid $IrisHi 0 130
    $G.FillPath($gb, $bounce)
    $gb.Dispose(); $bounce.Dispose()

    $pupilPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pupilPath.AddEllipse([single]($Shape.Pupil.X - $Shape.Pupil.R), [single]($Shape.Pupil.Y - $Shape.Pupil.R),
                          [single]($Shape.Pupil.R * 2), [single]($Shape.Pupil.R * 2))
    $G.FillPath((New-Solid $PupilInk), $pupilPath)
    $pupilPath.Dispose()

    # Key light high and towards the nose, a smaller catch-light low and out.
    foreach ($spec in @(@{ c = $Shape.Key; a = 240 }, @{ c = $Shape.Fill; a = 130 })) {
        $specPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $specPath.AddEllipse([single]($spec.c.X - $spec.c.R), [single]($spec.c.Y - $spec.c.R),
                             [single]($spec.c.R * 2), [single]($spec.c.R * 2))
        $G.FillPath((New-Solid 0xFFFFFF $spec.a), $specPath)
        $specPath.Dispose()
    }

    # Upper lid shadow across the top of the eye.
    $lidSave = $G.Save()
    $G.TranslateTransform([single]0.0, [single]3.0)
    $G.FillPath((New-Solid 0x5A3A28 92), $Shape.Lid)
    $G.Restore($lidSave)

    $G.Restore($save)

    # Lash band, then the detached lashes past the outer corner, then the lower line.
    $G.FillPath((New-Solid $LashInk 250), $Shape.Lash)

    foreach ($f in $Shape.Flutters) {
        $lashPen.Width = [single]$f.W
        $G.DrawCurve($lashPen, [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new([single]$f.P1[0], [single]$f.P1[1]),
            [System.Drawing.PointF]::new([single]$f.C[0], [single]$f.C[1]),
            [System.Drawing.PointF]::new([single]$f.P2[0], [single]$f.P2[1])))
    }

    $lowerPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $lowerPath.StartFigure()
    $lowerPath.AddBezier([single]-18.0, [single]5.5, [single]-7, [single]11.5,
                         [single]8, [single]9.5, [single]19.5, [single]1.5)
    $lashPen.Width = [single]1.1
    $lashPen.Color = New-C $LashInk 120
    $G.DrawPath($lashPen, $lowerPath)
    $lowerPath.Dispose()

    $lashPen.Color = New-C $LashInk 255
    $G.FillPath((New-Solid $LashInk 190), $Shape.Brow)
    $lashPen.Dispose()
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
        Draw-Glow $G ($X + $W * 0.30) ($Y + $H * 0.14) ($W * 0.60) $GlowCool 20
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

<#
    Renders the mark into its own buffer: flat colour zones first, then a parallel relief buffer
    holding how high each zone stands, then one lighting pass over the pair.

    Two buffers rather than hand-placed highlights because the bevel has to fall out of the
    geometry: the same paths that paint the star also raise it, so a designer moving a zone moves
    its shading with it. The buffer is padded because the drop shadow leaves the mark's own box.

    Zones are ramped over the *head* box even when rendering the cap crop, so the cap's colour is
    identical in the 16px plate and in the full head -- the crop is a window, not a different mark.
#>
function Get-MascotBitmap {
    param([int]$Side, [switch]$Mini)

    $box = if ($Mini) { $BoxStar } else { $BoxHead }
    $pad = [int][Math]::Ceiling($Side * 0.14) + 3
    $bw = $Side + 2 * $pad
    $fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb

    $colour = New-Object System.Drawing.Bitmap($bw, $bw, $fmt)
    $relief = New-Object System.Drawing.Bitmap($bw, $bw, $fmt)
    $cg = [System.Drawing.Graphics]::FromImage($colour)
    $rg = [System.Drawing.Graphics]::FromImage($relief)
    foreach ($g in @($cg, $rg)) {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::GammaCorrected
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    }
    $cg.Clear([System.Drawing.Color]::Transparent)
    $rg.Clear([System.Drawing.Color]::Black)

    $xform = New-SrcToDst $box $pad $pad $Side
    $cg.MultiplyTransform($xform.Matrix)
    $rg.MultiplyTransform($xform.Matrix)

    $top = $BoxHead.Y
    $bot = $BoxHead.Y + $BoxHead.H

    $skinB = New-ZoneBrush $top $bot $SkinHi 0xEFC3A6 $SkinLo 0.5
    $cg.FillPath($skinB, $Mascot.Skin)
    $skinB.Dispose()

    $starB = New-ZoneBrush $top $bot $GoldHi $GoldMid $GoldLo 0.46
    $cg.FillPath($starB, $Mascot.Star)
    $starB.Dispose()

    $clothB = New-ZoneBrush $top $bot $ClothHi $ClothMid $ClothLo 0.44
    $cg.FillPath($clothB, $Mascot.Cloth)
    $clothB.Dispose()

    # A dark edge on the silhouette: on a light Start tile or a white desktop the pale blue crown
    # otherwise runs straight into the backdrop.
    $rim = New-Object System.Drawing.Pen((New-C 0x0A1730 120), [single]1.1)
    $cg.DrawPath($rim, $Mascot.Cloth)
    $rim.Dispose()

    if (-not $Mini) {
        $shape = New-EyeShape
        foreach ($eye in $EyeAt) {
            $state = $cg.Save()
            $m = New-Object System.Drawing.Drawing2D.Matrix
            [void]$m.Translate([single]$eye.X, [single]$eye.Y)
            # The right-hand eye is authored as-is; the left is its mirror about the face's axis.
            if ($eye.X -lt $BoxHead.X + $BoxHead.W / 2.0) { [void]$m.Scale([single]-1.0, [single]1.0) }
            $cg.MultiplyTransform($m)
            Draw-MascotEye -G $cg -Eye $eye -Shape $shape
            $cg.Restore($state)
            $m.Dispose()
        }
        $shape.Sclera.Dispose(); $shape.Lid.Dispose(); $shape.Lash.Dispose(); $shape.Brow.Dispose()
    }

    # Relief. The skin is the base surface, the cloth stands proud of it, the star stands proud of
    # the cloth, and the eye sockets are pressed back in -- so the bevel catches a highlight along
    # the top of each socket and a shadow under the brow without anything being painted by hand.
    $b = New-ReliefBrush 0.0
    $rg.FillPath($b, $Mascot.Figure)
    $b.Dispose()
    $b = New-ReliefBrush $ClothLift
    $rg.FillPath($b, $Mascot.Cloth)
    $b.Dispose()
    $b = New-ReliefBrush $StarLift
    $rg.FillPath($b, $Mascot.Star)
    $b.Dispose()
    if (-not $Mini) {
        $b = New-ReliefBrush $SocketDip
        foreach ($eye in $EyeAt) {
            $rg.FillEllipse($b, [single]($eye.X - $EyeRx * 0.92), [single]($eye.Y - $EyeRy * 0.92),
                            [single]($EyeRx * 1.84), [single]($EyeRy * 1.84))
        }
        $b.Dispose()
    }

    $cg.Dispose(); $rg.Dispose()
    $xform.Matrix.Dispose()

    # Blur the relief so zone edges become sloped walls, then light it from the upper left.
    $rb = [Math]::Max(1, [int][Math]::Round($Side * $BevelSoftness, [System.MidpointRounding]::AwayFromZero))
    $rgb = [EvaniaMascot]::ToArgb($relief)
    $height = [EvaniaMascot]::BlurF32(
        [EvaniaMascot]::ToFloat([EvaniaMascot]::ChannelOf($rgb, 16), (1.0 / 255.0)), $bw, $bw, $rb)

    $cargb = [EvaniaMascot]::ToArgb($colour)
    # A full height change across the sloped wall is what "strength" means, so it has to be scaled
    # by the wall's width: the same number then reads the same at 16px and at 1500px.
    [EvaniaMascot]::Bevel($cargb, $bw, $bw, $height, ($BevelStrength * $rb * 0.5), $BevelSpecular, -0.72, -0.69)

    $alpha = [EvaniaMascot]::AlphaOf($cargb)

    $mark = New-Object System.Drawing.Bitmap($bw, $bw, $fmt)
    [EvaniaMascot]::FromArgb($mark, $cargb)
    $colour.Dispose(); $relief.Dispose()

    if ($Side -lt 34) { return $mark }        # a shadow at that size is just mud

    $rs = [Math]::Max(1, [int][Math]::Round($Side * 0.045, [System.MidpointRounding]::AwayFromZero))
    $soft = [EvaniaMascot]::BlurU8($alpha, $bw, $bw, $rs)
    $drop = [int][Math]::Round($Side * $ShadowDrop, [System.MidpointRounding]::AwayFromZero)

    # The shadow is the mark's own coverage, blurred, dimmed, and pushed down-right away from the
    # key light. Its alpha is folded into the plane here rather than through a ColorMatrix at draw
    # time: an ImageAttributes colour matrix applies to whichever image is next handed to
    # DrawImage, and when that was the mark itself the whole character came out black -- which only
    # the zone probes catch, because a black mark still has the right silhouette and the right
    # alpha, so every size and opacity check passes on it.
    $shadowArgb = [EvaniaMascot]::SolidWithAlpha($soft, 0x000000, ($ShadowAlpha / 255.0))
    $shadow = New-Object System.Drawing.Bitmap($bw, $bw, $fmt)
    [EvaniaMascot]::FromArgb($shadow, $shadowArgb)

    $final = New-Object System.Drawing.Bitmap($bw, $bw, $fmt)
    $fg = [System.Drawing.Graphics]::FromImage($final)
    $fg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $fg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $fg.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $fg.DrawImage($shadow, [System.Drawing.RectangleF]::new([single]0.0, [single](-$drop), [single]$bw, [single]$bw))
    $fg.DrawImage($mark, [System.Drawing.RectangleF]::new([single]0.0, [single]0.0, [single]$bw, [single]$bw))
    $fg.Dispose(); $shadow.Dispose(); $mark.Dispose()
    return $final
}

$Mascot = Initialize-Mascot -Path $MascotSource -StarBox $StarBox -Eyes $EyeAt -Rx $EyeRx -Ry $EyeRy
Write-Host ("mascot traced: cloth {0}px  skin {1}px  star {2}px  figure {3}px" -f `
    $Mascot.ClothPx, $Mascot.SkinPx, $Mascot.StarPx, $Mascot.FigurePx) -ForegroundColor DarkGray

$MascotCache = @{}

function Get-Mascot {
    param([int]$Side, [switch]$Mini)
    $key = "$Side$(if ($Mini) { 'm' } else { '' })"
    if (-not $MascotCache.ContainsKey($key)) {
        $MascotCache[$key] = Get-MascotBitmap -Side $Side -Mini:$Mini
    }
    return $MascotCache[$key]
}

# Places the mark on the canvas, centred at ($Cx, $Cy) with the mark's own box $Side wide.
function Draw-Mascot {
    param($G, [double]$Cx, [double]$Cy, [double]$Side, [switch]$Mini)
    $bmp = Get-Mascot -Side ([int][Math]::Max(8, [Math]::Round($Side))) -Mini:$Mini
    $total = $bmp.Width
    $G.DrawImage($bmp, [System.Drawing.RectangleF]::new(
        [single]($Cx - $total / 2.0), [single]($Cy - $total / 2.0), [single]$total, [single]$total))
}


<#
    Square tile geometry in one place, because the self-check has to know exactly where the mark
    landed to sample it. Design units are 0..1000 across the plate so every scale shares one spec.
#>
function Get-SquareMarkLayout {
    param([double]$S, [switch]$Mini)
    $inset = if ($Mini) { 0.0 } else { 0.045 }
    $pad = $S * $inset
    $plateSide = $S - 2 * $pad
    $u = $plateSide / 1000.0
    return [pscustomobject]@{
        PlateX    = $pad
        PlateY    = $pad
        PlateSide = $plateSide
        Radius    = $plateSide * $(if ($Mini) { 0.225 } else { 0.205 })
        MarkCx    = $pad + $plateSide / 2.0
        MarkCy    = $pad + $plateSide / 2.0 + $u * $(if ($Mini) { 30 } else { 8 })
        MarkSide  = $plateSide * $(if ($Mini) { 0.94 } else { 0.80 })
    }
}

# Where the source point (sx, sy) lands in the finished raster.
function Get-SourcePixel {
    param([object]$Box, [double]$Cx, [double]$Cy, [double]$Side, [double]$Sx, [double]$Sy)
    $s = [Math]::Min($Side / $Box.W, $Side / $Box.H)
    return @(
        [int][Math]::Round($Cx - ($Box.W * $s) / 2.0 + ($Sx - $Box.X) * $s, [System.MidpointRounding]::AwayFromZero),
        [int][Math]::Round($Cy - ($Box.H * $s) / 2.0 + ($Sy - $Box.Y) * $s, [System.MidpointRounding]::AwayFromZero))
}

function Get-PixelAt {
    param([int[]]$Argb, [int]$W, [int]$X, [int]$Y)
    if ($X -lt 0 -or $Y -lt 0 -or $X -ge $W -or $Y -ge $W) { return $null }
    $c = $Argb[$Y * $W + $X]
    return [pscustomobject]@{
        A = ($c -shr 24) -band 0xFF
        R = ($c -shr 16) -band 0xFF
        G = ($c -shr 8) -band 0xFF
        B = $c -band 0xFF
    }
}

<#
    Samples the rendered tile at the source coordinates that matter and reports what colour family
    each one came back as. This is how the recolour is judged when nobody can look at it: the star
    has to come back gold, the hair and cap blue, the face warm, the irises blue, and the arc the
    old rim occupied must have become skin rather than staying black.
#>
function Get-MascotAudit {
    param([byte[]]$Bytes, [int]$W, [switch]$Mini)

    $argb = [EvaniaMascot]::ArgbFromBytes($Bytes)
    $lay = Get-SquareMarkLayout $W -Mini:$Mini
    $box = if ($Mini) { $BoxStar } else { $BoxHead }

    function Classify([int[]]$Argb, [double]$Sx, [double]$Sy) {
        $p = Get-SourcePixel $box $lay.MarkCx $lay.MarkCy $lay.MarkSide $Sx $Sy
        $px = Get-PixelAt $Argb $W $p[0] $p[1]
        if (-not $px) { return 'offcanvas', $null }
        $hi = [Math]::Max($px.R, [Math]::Max($px.G, $px.B))
        $lo = [Math]::Min($px.R, [Math]::Min($px.G, $px.B))
        $fam = if ($px.A -lt 24) { 'clear' }
            elseif ($hi -lt 96) { 'dark' }
            elseif ($hi -lt 250 -and ($hi - $lo) -lt 26) { 'grey' }
            elseif ($px.B -gt $px.R + 18) { 'blue' }
            # Gold and skin both run red-over-blue, so the green channel is what separates them:
            # metal sits far above blue, warm skin sits only a little above it.
            elseif ($px.R -gt 150 -and ($px.R - $px.B) -gt 90 -and ($px.G - $px.B) -gt 70) { 'gold' }
            elseif ($px.R -gt $px.B + 12) { 'warm' }
            else { 'pale' }
        return , @($fam, $px)
    }

    $probes = [ordered]@{
        'cap band'  = @(167.5, 116.0)
        'star'      = @(167.5,  85.5)
        'hair left' = @( 82.0, 200.0)
    }
    if (-not $Mini) {
        # Every pair here is parenthesised deliberately: the comma binds tighter than + in
        # PowerShell, so @($a.X + 1.0, $a.Y - 2.0) actually parses as $a.X + (1.0, $a.Y) - 2.0 and
        # dies trying to add a Double to an Object[].
        #
        # The eyes are not probed as points; see Get-EyeRegionCounts.
        $probes['hair right']   = @(253.0, 200.0)
        $probes['forehead']     = @(167.5, 140.0)
        $probes['chin']         = @(167.5, 208.0)
        # Skin where the bottom of the old rim used to sit, clear of the new eye.
        $probes['rim gone L'] = @(137.0, 190.0)
        $probes['rim gone R'] = @(198.0, 190.0)
    }

    $found = [ordered]@{}
    foreach ($k in $probes.Keys) {
        $r = Classify $argb $probes[$k][0] $probes[$k][1]
        $px = $r[1]
        $found[$k] = if ($px) { '{0} #{1:X2}{2:X2}{3:X2}' -f $r[0], $px.R, $px.G, $px.B } else { $r[0] }
    }
    return $found
}

<#
    Counts the colour families inside each eye's box. Point probes cannot judge an eye: even at
    600px the whites are a sliver two or three pixels wide, and a single sample lands on the pupil
    or on a lash. The box as a whole has to contain whites, iris blue, and lash dark in the
    proportions the drawing calls for, which is what this reports.

    The box is deliberately short of the brow (local y -13 and up) so a brow stroke cannot be
    mistaken for a lash, and it sits well inside the face opening so no hair can leak in as blue.
#>
function Get-EyeRegionCounts {
    param([int[]]$Argb, [int]$W, [object]$Lay, [object]$Box)

    $out = New-Object System.Collections.Generic.List[object]
    foreach ($eye in $EyeAt) {
        $p0 = Get-SourcePixel $Box $Lay.MarkCx $Lay.MarkCy $Lay.MarkSide ($eye.X - 20.0) ($eye.Y - 13.0)
        $p1 = Get-SourcePixel $Box $Lay.MarkCx $Lay.MarkCy $Lay.MarkSide ($eye.X + 20.0) ($eye.Y + 9.0)
        $pale = 0; $blue = 0; $dark = 0; $tot = 0
        for ($y = [Math]::Min($p0[1], $p1[1]); $y -le [Math]::Max($p0[1], $p1[1]); $y++) {
            for ($x = [Math]::Min($p0[0], $p1[0]); $x -le [Math]::Max($p0[0], $p1[0]); $x++) {
                if ($x -lt 0 -or $y -lt 0 -or $x -ge $W -or $y -ge $W) { continue }
                $c = $Argb[$y * $W + $x]
                if ((($c -shr 24) -band 0xFF) -lt 200) { continue }
                $r = ($c -shr 16) -band 0xFF; $g = ($c -shr 8) -band 0xFF; $b = $c -band 0xFF
                $hi = [Math]::Max($r, [Math]::Max($g, $b))
                $lo = [Math]::Min($r, [Math]::Min($g, $b))
                $tot++
                if ($b -gt $r + 18) { $blue++ }
                elseif ($hi -gt 200 -and ($hi - $lo) -lt 40) { $pale++ }
                elseif ($hi -lt 110) { $dark++ }
            }
        }
        $out.Add([pscustomobject]@{ Eye = "$([int]$eye.X)"; Tot = $tot; Pale = $pale; Blue = $blue; Dark = $dark })
    }
    return $out
}

<#
    Proves the bevel is a real surface rather than a gradient stuck on top. Renders the mark buffer
    twice -- once with the lighting pass, once without -- and correlates the difference against the
    outward normal of the silhouette. Edges that face the key light must have got brighter and edges
    that face away darker; a flat wash would show no correlation at all.

    Measured on the mark buffer rather than on a finished tile because every layout paints an
    opaque plate under the mark: inside a tile the character's own outline has no alpha edge left to
    find, and the check would quietly run over zero pixels.
#>
function Get-BevelCorrelation {
    param([int]$Side = 256)

    $render = {
        param([double]$Strength)
        $keptS = $script:BevelStrength
        $keptK = $script:BevelSpecular
        $script:BevelStrength = $Strength
        $script:BevelSpecular = if ($Strength -gt 0) { $keptK } else { 0.0 }
        $MascotCache.Clear()
        try {
            $bmp = Get-MascotBitmap -Side $Side
            $pixels = [EvaniaMascot]::ToArgb($bmp)
            $edge = $bmp.Width
            $bmp.Dispose()
            return , @($pixels, $edge)
        } finally {
            $script:BevelStrength = $keptS
            $script:BevelSpecular = $keptK
            $MascotCache.Clear()
        }
    }

    $litRun = & $render $script:BevelStrength
    $flatRun = & $render 0.0
    $lit = $litRun[0]
    $flat = $flatRun[0]
    $W = [int]$litRun[1]

    # Planes pulled out in one pass each: the per-pixel loop below must not call back into
    # PowerShell helpers 65,000 times, which is what made this one check cost more than every
    # render in the build put together.
    $fa = [EvaniaMascot]::AlphaOf($flat)

    $sumT = 0.0; $sumD = 0.0; $sumTD = 0.0; $sumTT = 0.0; $sumDD = 0.0; $sumA = 0.0; $n = 0
    for ($y = 1; $y -lt ($W - 1); $y++) {
        $row = $y * $W
        for ($x = 1; $x -lt ($W - 1); $x++) {
            $i = $row + $x
            if ($fa[$i] -le 200) { continue }
            $aL = $fa[$i - 1]; $aR = $fa[$i + 1]; $aU = $fa[$i - $W]; $aD = $fa[$i + $W]
            if ($aL -gt 200 -and $aR -gt 200 -and $aU -gt 200 -and $aD -gt 200) { continue }

            # Outward normal: alpha falls away in the direction the surface ends, so the step from
            # inside to outside gives its direction, and the unit vector its orientation alone.
            $nx = [double]($aL - $aR)
            $ny = [double]($aU - $aD)
            $m = [Math]::Sqrt($nx * $nx + $ny * $ny)
            if ($m -lt 1e-6) { continue }
            $nx /= $m; $ny /= $m
            $facing = -($nx * 0.72 + $ny * 0.69)     # key light from the upper left

            $cl = $lit[$i]; $cf = $flat[$i]
            $d = (0.299 * ((($cl -shr 16) -band 0xFF) - (($cf -shr 16) -band 0xFF)) +
                  0.587 * ((($cl -shr 8) -band 0xFF) - (($cf -shr 8) -band 0xFF)) +
                  0.114 * (($cl -band 0xFF) - ($cf -band 0xFF))) / 255.0

            $sumT += $facing; $sumD += $d; $sumTD += $facing * $d
            $sumA += [Math]::Abs($d)
            $sumTT += $facing * $facing; $sumDD += $d * $d; $n++
        }
    }
    if ($n -lt 20) { return [pscustomobject]@{ N = $n; Corr = 0.0; Spread = 0.0 } }
    $cov = $sumTD - ($sumT * $sumD) / $n
    $vx = $sumTT - ($sumT * $sumT) / $n
    $vy = $sumDD - ($sumD * $sumD) / $n
    $den = [Math]::Sqrt($vx * $vy)
    return [pscustomobject]@{
        N = $n
        Corr = if ($den -lt 1e-9) { 0.0 } else { $cov / $den }
        Spread = $sumA / $n
    }
}

# Rasterises a traced path back over the mask it came from and reports the disagreement.
function Get-TraceMismatch {
    param($Path, [byte[]]$Mask, [int]$W, [int]$H)
    $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Solid 0xFFFFFF), $Path)
    $g.Dispose()
    $err = [EvaniaMascot]::MaskMismatch($bmp, $Mask, $W, $H)
    $bmp.Dispose()
    return $err
}

<#
    Square tile: the plate, then the mark.
#>
function Draw-SquareMark {
    param($G, [double]$Ox, [double]$Oy, [double]$S, [switch]$Mini, [switch]$MaskOnly)

    $lay = Get-SquareMarkLayout $S -Mini:$Mini
    $plateX = $Ox + $lay.PlateX
    $plateY = $Oy + $lay.PlateY
    $plateSide = $lay.PlateSide
    $u = $plateSide / 1000.0

    if ($MaskOnly) {
        $mask = New-RoundedPath $plateX $plateY $plateSide $plateSide $lay.Radius
        $G.FillPath([System.Drawing.Brushes]::White, $mask)
        $mask.Dispose()
        return
    }

    Draw-TilePlate $G $plateX $plateY $plateSide $plateSide $lay.Radius `
        -RimRatio $(if ($Mini) { 0 } else { 0.0075 }) -Sheen

    # Everything above the plate is clipped to it: the glow's falloff otherwise floats a few
    # faint pixels outside the tile silhouette, which smudges on a light Start or splash backdrop.
    $clip = New-RoundedPath $plateX $plateY $plateSide $plateSide $lay.Radius
    $save = $G.Save()
    $G.SetClip($clip)

    # Glows are kept inside the plate's own half-width: GDI+ clips with a hard edge while the plate
    # silhouette is anti-aliased, so anything that reaches the clip boundary comes back as a faint
    # ring of paint outside the tile -- invisible on a dark Start, a halo on a light one.
    $cx = $plateX + $plateSide / 2.0
    $cy = $plateY + $plateSide / 2.0
    Draw-Glow $G $cx ($cy + $u * 30) ($plateSide * 0.46) $GlowCool 44
    if (-not $Mini) {
        Draw-Glow $G $cx ($cy + $u * 190) ($plateSide * 0.40) $GlowWarm 24
    }
    Draw-Mascot $G $lay.MarkCx $lay.MarkCy $lay.MarkSide -Mini:$Mini

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

    # Mark sized off the banner height so the lockup holds its proportions at every scale. Two
    # constraints set the numbers: the head is portrait, so its own width is only 0.859 of its
    # height and the left edge must still clear the inset plate; and the wordmark's box has to
    # start past its right edge. Both hold at 310px and at 1240px with the same fractions.
    $mside = $H * 0.72
    $mcx = $X + $W * 0.208
    $mcy = $Y + $H * 0.50
    # Kept wholly inside the plate: GDI+ clips with a hard edge while the plate's own silhouette is
    # anti-aliased, so paint that reaches the clip boundary survives as a faint ring outside it.
    Draw-Glow $G $mcx ($mcy - $H * 0.02) ($H * 0.34) $GlowCool 40
    Draw-Mascot $G $mcx $mcy $mside

    # The wordmark autofits the room that is left, so no DPI scale can push it off the plate.
    $boxX = $X + $W * 0.380
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

# The vector has to still say what the drawing says: every zone is rasterised back over its own
# mask. Deviation is dominated by the source's own anti-aliased fringe, so the bound is loose, but
# it catches a swapped source, a mis-set epsilon, or a trace that dropped a contour.
foreach ($zone in @(
        @{ n = 'cloth';  p = $Mascot.Cloth;  m = $Mascot.ClothMask },
        @{ n = 'skin';   p = $Mascot.Skin;   m = $Mascot.SkinMask },
        @{ n = 'star';   p = $Mascot.Star;   m = $Mascot.StarMask },
        @{ n = 'figure'; p = $Mascot.Figure; m = $Mascot.FigureMask })) {
    $err = Get-TraceMismatch $zone.p $zone.m $Mascot.W $Mascot.H
    '  {0,-58} {1:N4}' -f "trace fidelity ($($zone.n) vs its source mask)", $err
    if ($err -gt 0.02) {
        $fail.Add("trace fidelity: $($zone.n) disagrees with the source mask over $([Math]::Round($err * 100, 2))% of the canvas")
    }
}

# Where each part of the character landed, read back out of the finished pixels. Probed on the
# 600px asset rather than the 150px one because an eye is 42 source pixels wide: at 150px that is
# seven pixels, and a single probe lands on the wrong feature. The smallest sizes get their own
# probe below, where the question is only whether the star survives.
$auditBytes = Render-PngBytes 600 600 'Tile'
$audit = Get-MascotAudit $auditBytes 600
$want = [ordered]@{
    'cap band'    = 'blue'   # the uniform under the star
    'star'        = 'gold'
    'hair left'   = 'blue'
    'hair right'  = 'blue'
    'forehead'    = 'warm'   # skin
    'chin'        = 'warm'
    'rim gone L'  = 'warm'   # the arc the old rim filled is skin now
    'rim gone R'  = 'warm'
}
foreach ($k in $want.Keys) {
    $got = ('' + $audit[$k]) -split ' '
    '  {0,-58} {1,-6} want {2}   {3}' -f "zone probe $k", $got[0], $want[$k], $audit[$k]
    if ($got[0] -ne $want[$k]) {
        $fail.Add("zone probe $k came back '$($got[0])', expected '$($want[$k])' ($($audit[$k]))")
    }
}

# The replaced eyes: whites, iris and lash all have to be present in both sockets, and the two
# sockets have to agree -- they are drawn from one shape and its mirror, so a mismatch means the
# mirroring broke somewhere. Judged at 600px; the smaller sizes are reported alongside because a
# feature that is two source pixels wide has no right to survive at 44px, and it is better to see
# where the mark stops reading than to pretend the check covers it.
$eyeArgb = [EvaniaMascot]::ArgbFromBytes($auditBytes)
$eyeLay = Get-SquareMarkLayout 600
$eyes = @(Get-EyeRegionCounts $eyeArgb 600 $eyeLay $BoxHead)
foreach ($e in $eyes) {
    '  {0,-58} white {1:N1} pct  iris {2:N1} pct  lash {3:N1} pct  of {4}px' -f `
        "eye at x$($e.Eye)", (100.0 * $e.Pale / $e.Tot), (100.0 * $e.Blue / $e.Tot), (100.0 * $e.Dark / $e.Tot), $e.Tot
    if ($e.Tot -lt 100) { $fail.Add("eye at x$($e.Eye): box is only $($e.Tot)px, nothing to judge") }
    if ($e.Pale / $e.Tot -lt 0.05) { $fail.Add("eye at x$($e.Eye): no whites where the eye should be") }
    if ($e.Blue / $e.Tot -lt 0.04) { $fail.Add("eye at x$($e.Eye): no iris colour where the eye should be") }
    if ($e.Dark / $e.Tot -lt 0.02) { $fail.Add("eye at x$($e.Eye): no lash on the eye") }
}
if ($eyes.Count -eq 2) {
    foreach ($m in @('Pale', 'Blue', 'Dark')) {
        $a = $eyes[0].($m) / [Math]::Max(1, $eyes[0].Tot)
        $b = $eyes[1].($m) / [Math]::Max(1, $eyes[1].Tot)
        $worst = [Math]::Max($a, $b)
        if ($worst -gt 0 -and ([Math]::Abs($a - $b) / $worst) -gt 0.35) {
            $fail.Add("the two eyes disagree on $m ($([Math]::Round(100*$a,1)) pct vs $([Math]::Round(100*$b,1)) pct) - the mirror is off")
        }
    }
}

foreach ($t in @(44, 88, 150)) {
    $tinyArgb = [EvaniaMascot]::ArgbFromBytes((Render-PngBytes $t $t 'Tile'))
    $tinyLay = Get-SquareMarkLayout $t
    $sum = @(Get-EyeRegionCounts $tinyArgb $t $tinyLay $BoxHead)
    $tp = ($sum | Measure-Object -Property Pale -Sum).Sum
    $tb = ($sum | Measure-Object -Property Blue -Sum).Sum
    $td = ($sum | Measure-Object -Property Dark -Sum).Sum
    '  {0,-58} white {1} px  iris {2} px  lash {3} px' -f "eye structure at ${t}px", $tp, $tb, $td
    # Below a certain size the eyes can only be suggested; demanding structure there would be
    # demanding a lie. Zero, though, means the feature has vanished completely.
    if ($t -le 44 -and (($tb + $td) -eq 0)) {
        $fail.Add("at ${t}px the eyes carry no ink at all - the face reads as blank skin")
    }
}

$auditMini = Get-MascotAudit (Render-PngBytes 16 16 'Plate' -Mini) 16 -Mini
foreach ($k in @('star', 'cap band')) {
    $got = ('' + $auditMini[$k]) -split ' '
    '  {0,-58} {1,-6} want {2}   {3}' -f "16px crop probe $k", $got[0], $want[$k], $auditMini[$k]
    if ($got[0] -ne $want[$k]) {
        $fail.Add("16px crop: $k came back '$($got[0])', expected '$($want[$k])' - the star must survive the smallest size")
    }
}

$bev = Get-BevelCorrelation
'  {0,-58} corr {1:N2}, mean shift {2:N1} pct, {3} edge px' -f `
    'bevel lights the silhouette as a surface', $bev.Corr, (100 * $bev.Spread), $bev.N
if ($bev.N -lt 20) { $fail.Add('bevel check found no silhouette edge to measure') }
if ($bev.Corr -lt 0.30) {
    $fail.Add("bevel correlates only $([Math]::Round($bev.Corr,2)) with the light direction - the emboss is not a surface")
}
# Signed deltas cancel across a symmetric rim, so the size of the effect is the mean absolute shift.
if ($bev.Spread -lt 0.02) { $fail.Add('bevel moved the edge pixels by almost nothing') }

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
