$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$brandRoot = Join-Path $repoRoot "assets\brand"
$pngRoot = Join-Path $brandRoot "png"
$sizes = @(16, 24, 32, 48, 64, 128, 256)

New-Item -ItemType Directory -Path $pngRoot -Force | Out-Null

function New-RoundedRectanglePath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-AppIconBitmap([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scale = $size / 256.0
    $graphics.ScaleTransform($scale, $scale)

    $tile = New-RoundedRectanglePath 8 8 240 240 52
    $tileBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml("#F7F2EA"))
    $graphics.FillPath($tileBrush, $tile)

    $cycle = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $cycle.StartFigure()
    $cycle.AddLine(68, 200, 184, 200)
    $cycle.AddBezier(184, 200, 192.84, 200, 200, 192.84, 200, 184)
    $cycle.AddLine(200, 184, 200, 72)
    $cycle.AddBezier(200, 72, 200, 63.16, 192.84, 56, 184, 56)
    $cycle.AddLine(184, 56, 56, 56)
    $inkPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml("#283335"), 40)
    $inkPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $inkPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $inkPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawPath($inkPen, $cycle)

    $sagePen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml("#6D9D85"), 40)
    $sagePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $sagePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($sagePen, 56, 108, 56, 148)

    $sagePen.Dispose()
    $inkPen.Dispose()
    $cycle.Dispose()
    $tileBrush.Dispose()
    $tile.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$pngPaths = @()
foreach ($size in $sizes) {
    $output = Join-Path $pngRoot "aq-tracker-$size.png"
    $bitmap = New-AppIconBitmap $size
    $bitmap.Save($output, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $pngPaths += $output
}

# ICO supports PNG-compressed entries. Keeping all common Windows sizes in one
# container avoids palette loss and preserves alpha around the rounded tile.
$images = @()
foreach ($pngPath in $pngPaths) {
    # Unary comma keeps each byte array as one ICO image instead of letting the
    # PowerShell pipeline flatten every PNG into individual bytes.
    $images += ,([System.IO.File]::ReadAllBytes($pngPath))
}
$icoPath = Join-Path $brandRoot "aq-tracker.ico"
$stream = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$images.Count)
$offset = 6 + (16 * $images.Count)
for ($index = 0; $index -lt $images.Count; $index++) {
    $size = $sizes[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$index].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Dispose()
$stream.Dispose()

Write-Host "Exported $($sizes.Count) PNG sizes and $icoPath"
