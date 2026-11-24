<#
    Generates the Future Speed application icon (Assets\FutureSpeed.ico).

    The mark is original: a rounded square with a blue-to-teal gradient and two
    white forward chevrons over a speed line - "a faster future for your files".
    Nothing here is copied from any existing product.

    Run:  powershell -ExecutionPolicy Bypass -File build\make-icon.ps1
#>

[CmdletBinding()]
param(
    [string]$OutputPath
)

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $scriptDir '..\src\FutureSpeed.App\Assets\FutureSpeed.ico'
}

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngStreams = @()

function New-RoundedPath {
    param([int]$Size, [int]$Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($Size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $Size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $radius = [Math]::Max(3, [int]($size * 0.22))
    $shape = New-RoundedPath -Size $size -Radius $radius

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 20, 102, 204),
        [System.Drawing.Color]::FromArgb(255, 16, 170, 150))

    $g.FillPath($brush, $shape)

    # Speed line under the chevrons.
    $linePen = New-Object System.Drawing.Pen(
        ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)),
        [Math]::Max(1.0, $size * 0.055))
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($linePen, [single]($size * 0.17), [single]($size * 0.76), [single]($size * 0.62), [single]($size * 0.76))

    # Two forward chevrons.
    $chevronPen = New-Object System.Drawing.Pen(
        ([System.Drawing.Color]::White),
        [Math]::Max(1.3, $size * 0.095))
    $chevronPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $chevronPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $chevronPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    foreach ($offset in @(0.0, 0.26)) {
        $x1 = [single]($size * (0.24 + $offset))
        $x2 = [single]($size * (0.44 + $offset))
        $points = @(
            (New-Object System.Drawing.PointF($x1, [single]($size * 0.26))),
            (New-Object System.Drawing.PointF($x2, [single]($size * 0.48))),
            (New-Object System.Drawing.PointF($x1, [single]($size * 0.62)))
        )
        $g.DrawLines($chevronPen, $points)
    }

    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $pngStreams += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
}

# --- assemble a PNG-based .ico (Vista format) ------------------------------
$outFull = [System.IO.Path]::GetFullPath($OutputPath)
$outDir = [System.IO.Path]::GetDirectoryName($outFull)
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$fs = [System.IO.File]::Create($outFull)
$writer = New-Object System.IO.BinaryWriter($fs)

$writer.Write([uint16]0)                       # reserved
$writer.Write([uint16]1)                       # type: icon
$writer.Write([uint16]$pngStreams.Count)       # image count

$offset = 6 + (16 * $pngStreams.Count)
foreach ($entry in $pngStreams) {
    $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $writer.Write([byte]$dim)                  # width
    $writer.Write([byte]$dim)                  # height
    $writer.Write([byte]0)                      # palette
    $writer.Write([byte]0)                      # reserved
    $writer.Write([uint16]1)                    # color planes
    $writer.Write([uint16]32)                   # bits per pixel
    $writer.Write([uint32]$entry.Bytes.Length)  # size of image data
    $writer.Write([uint32]$offset)              # offset of image data
    $offset += $entry.Bytes.Length
}

foreach ($entry in $pngStreams) {
    $writer.Write($entry.Bytes)
}

$writer.Flush()
$writer.Dispose()
$fs.Dispose()

Write-Output "Icon written: $outFull ($([int]((Get-Item $outFull).Length / 1024)) KB, $($pngStreams.Count) sizes)"
