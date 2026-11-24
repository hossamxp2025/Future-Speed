<#
    Generates the project's brand images for GitHub:

      docs/images/logo.png            512x512  the mark alone, transparent background
      docs/images/banner.png         1280x320  README header
      docs/images/social-preview.png 1280x640  GitHub "Social preview" (Settings > General)

    Everything is drawn from code with the Future Speed palette, so the images can be
    regenerated after any identity change and nothing is copied from another product.

    Run:  powershell -ExecutionPolicy Bypass -File build\make-brand-images.ps1
#>

[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptDir '..\docs\images'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }

# --- palette (identical to Themes/Palette.*.xaml) --------------------------
$accent     = [System.Drawing.Color]::FromArgb(255, 20, 102, 204)   # #1466CC
$secondary  = [System.Drawing.Color]::FromArgb(255, 16, 170, 150)   # #10AA96
$ink        = [System.Drawing.Color]::FromArgb(255, 15, 20, 27)     # #0F141B
$inkLight   = [System.Drawing.Color]::FromArgb(255, 24, 40, 60)
$textMuted  = [System.Drawing.Color]::FromArgb(255, 154, 170, 191)  # #9AAABF

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Mark {
    param(
        [System.Drawing.Graphics]$G,
        [single]$X, [single]$Y, [single]$Size
    )

    $shape = New-RoundedPath -X $X -Y $Y -W $Size -H $Size -Radius ($Size * 0.22)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF($X, $Y)),
        (New-Object System.Drawing.PointF(($X + $Size), ($Y + $Size))),
        $accent, $secondary)
    $G.FillPath($brush, $shape)
    $brush.Dispose(); $shape.Dispose()

    # speed line under the chevrons
    $linePen = New-Object System.Drawing.Pen(
        ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), ($Size * 0.055))
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $G.DrawLine($linePen, ($X + $Size * 0.17), ($Y + $Size * 0.76), ($X + $Size * 0.62), ($Y + $Size * 0.76))
    $linePen.Dispose()

    # two forward chevrons
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, ($Size * 0.095))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    foreach ($offset in @(0.0, 0.26)) {
        $points = @(
            (New-Object System.Drawing.PointF(($X + $Size * (0.24 + $offset)), ($Y + $Size * 0.26))),
            (New-Object System.Drawing.PointF(($X + $Size * (0.44 + $offset)), ($Y + $Size * 0.48))),
            (New-Object System.Drawing.PointF(($X + $Size * (0.24 + $offset)), ($Y + $Size * 0.62)))
        )
        $G.DrawLines($pen, $points)
    }
    $pen.Dispose()
}

function New-Canvas {
    param([int]$Width, [int]$Height, [bool]$Transparent = $false)
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    if (-not $Transparent) {
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.Point(0, 0)),
            (New-Object System.Drawing.Point($Width, $Height)),
            $ink, $inkLight)
        $g.FillRectangle($bg, 0, 0, $Width, $Height)
        $bg.Dispose()

        # faint diagonal "speed" streaks
        $streak = New-Object System.Drawing.Pen(([System.Drawing.Color]::FromArgb(14, 255, 255, 255)), 2)
        for ($x = -$Height; $x -lt $Width; $x += 26) {
            $g.DrawLine($streak, $x, $Height, ($x + $Height), 0)
        }
        $streak.Dispose()

        # accent glow bottom-right
        $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glowPath.AddEllipse(($Width * 0.55), ($Height * 0.2), ($Width * 0.75), ($Height * 1.4))
        $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
        $glow.CenterColor = [System.Drawing.Color]::FromArgb(70, $secondary.R, $secondary.G, $secondary.B)
        $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
        $g.FillPath($glow, $glowPath)
        $glow.Dispose(); $glowPath.Dispose()
    }

    return @{ Bitmap = $bmp; Graphics = $g }
}

function Draw-Text {
    param(
        [System.Drawing.Graphics]$G,
        [string]$Text,
        [string]$Family,
        [single]$Size,
        [System.Drawing.FontStyle]$Style,
        [System.Drawing.Color]$Color,
        [single]$X, [single]$Y,
        [bool]$RightToLeft = $false,
        [single]$MaxWidth = 0
    )
    $font = New-Object System.Drawing.Font($Family, $Size, $Style, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = New-Object System.Drawing.SolidBrush($Color)
    $format = New-Object System.Drawing.StringFormat
    if ($RightToLeft) { $format.FormatFlags = [System.Drawing.StringFormatFlags]::DirectionRightToLeft }

    if ($MaxWidth -gt 0) {
        $rect = New-Object System.Drawing.RectangleF($X, $Y, $MaxWidth, 400)
        $G.DrawString($Text, $font, $brush, $rect, $format)
    } else {
        $G.DrawString($Text, $font, $brush, $X, $Y, $format)
    }

    $font.Dispose(); $brush.Dispose(); $format.Dispose()
}

function Draw-Chip {
    param(
        [System.Drawing.Graphics]$G,
        [string]$Text,
        [single]$X, [single]$Y,
        [single]$FontSize = 20
    )
    $font = New-Object System.Drawing.Font('Segoe UI', $FontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $size = $G.MeasureString($Text, $font)
    [single]$w = $size.Width + 28
    [single]$h = $size.Height + 16

    $path = New-RoundedPath -X $X -Y $Y -W $w -H $h -Radius ($h / 2)
    $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(38, 255, 255, 255))
    [void]$G.FillPath($fill, $path)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 255, 255, 255), 1)
    [void]$G.DrawPath($pen, $path)

    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    [void]$G.DrawString($Text, $font, $brush, ($X + 14), ($Y + 8))

    $fill.Dispose(); $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $font.Dispose()
    Write-Output $w
}

# ===========================================================================
# 1) logo.png — the mark alone
# ===========================================================================
$canvas = New-Canvas -Width 512 -Height 512 -Transparent $true
Draw-Mark -G $canvas.Graphics -X 36 -Y 36 -Size 440
$logoPath = Join-Path $OutputDirectory 'logo.png'
$canvas.Graphics.Dispose()
$canvas.Bitmap.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Bitmap.Dispose()
Write-Output "logo.png"

# ===========================================================================
# 2) banner.png — README header, 1280x320
# ===========================================================================
$canvas = New-Canvas -Width 1280 -Height 320
$g = $canvas.Graphics

Draw-Mark -G $g -X 72 -Y 88 -Size 144

Draw-Text -G $g -Text 'Future Speed' -Family 'Segoe UI' -Size 64 `
    -Style ([System.Drawing.FontStyle]::Bold) -Color ([System.Drawing.Color]::White) -X 256 -Y 92 | Out-Null
Draw-Text -G $g -Text 'File Transfer' -Family 'Segoe UI' -Size 40 `
    -Style ([System.Drawing.FontStyle]::Regular) -Color ([System.Drawing.Color]::FromArgb(255, 43, 192, 168)) -X 258 -Y 160


Draw-Text -G $g -Text 'مستقبل أسرع لنقل ملفاتك' -Family 'Segoe UI' -Size 30 `
    -Style ([System.Drawing.FontStyle]::Regular) -Color $textMuted -X 1208 -Y 180 -RightToLeft $true | Out-Null

Draw-Text -G $g -Text 'A faster future for your files' -Family 'Segoe UI' -Size 26 `
    -Style ([System.Drawing.FontStyle]::Regular) -Color ([System.Drawing.Color]::FromArgb(170, 154, 170, 191)) -X 258 -Y 210 | Out-Null

$x = 256.0
foreach ($chip in @('.NET 8', 'WPF · MVVM', 'SHA-256', 'RTL + LTR', 'Explorer menu')) {
    $x += (Draw-Chip -G $g -Text $chip -X $x -Y 258 -FontSize 18) + 10
}

$bannerPath = Join-Path $OutputDirectory 'banner.png'
$g.Dispose()
$canvas.Bitmap.Save($bannerPath, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Bitmap.Dispose()
Write-Output "banner.png"

# ===========================================================================
# 3) social-preview.png — GitHub social preview, 1280x640
# ===========================================================================
$canvas = New-Canvas -Width 1280 -Height 640
$g = $canvas.Graphics

Draw-Mark -G $g -X 96 -Y 128 -Size 180

Draw-Text -G $g -Text 'Future Speed' -Family 'Segoe UI' -Size 78 `
    -Style ([System.Drawing.FontStyle]::Bold) -Color ([System.Drawing.Color]::White) -X 316 -Y 140 | Out-Null
Draw-Text -G $g -Text 'File Transfer' -Family 'Segoe UI' -Size 56 `
    -Style ([System.Drawing.FontStyle]::Regular) -Color ([System.Drawing.Color]::FromArgb(255, 43, 192, 168)) -X 318 -Y 232 | Out-Null

Draw-Text -G $g -Text 'مستقبل أسرع لنقل ملفاتك' -Family 'Segoe UI' -Size 40 `
    -Style ([System.Drawing.FontStyle]::Regular) -Color ([System.Drawing.Color]::FromArgb(245, 232, 238, 246)) -X 1184 -Y 330 -RightToLeft $true | Out-Null

Draw-Text -G $g -Text 'Fast, verified file transfers for Windows — Arabic RTL & English' `
    -Family 'Segoe UI' -Size 27 -Style ([System.Drawing.FontStyle]::Regular) -Color $textMuted -X 100 -Y 416 | Out-Null

$x = 100.0
foreach ($chip in @('C# · .NET 8', 'WPF · MVVM', 'SHA-256 verify', 'Resume', 'Queue · Sync · Backup')) {
    $x += (Draw-Chip -G $g -Text $chip -X $x -Y 476 -FontSize 23) + 12
}

# bottom accent rule
$rule = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.Point(0, 0)), (New-Object System.Drawing.Point(1280, 0)), $accent, $secondary)
$g.FillRectangle($rule, 0, 628, 1280, 12)
$rule.Dispose()

$socialPath = Join-Path $OutputDirectory 'social-preview.png'
$g.Dispose()
$canvas.Bitmap.Save($socialPath, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Bitmap.Dispose()
Write-Output "social-preview.png"

Write-Output "`nSaved to: $OutputDirectory"
