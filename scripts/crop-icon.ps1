param(
    [Parameter(Mandatory = $true)]
    [string]$InputImage,

    [Parameter(Mandatory = $true)]
    [string]$OutputImage
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$bitmap = New-Object System.Drawing.Bitmap($InputImage)

try {
    $squareSize = [Math]::Min($bitmap.Width, $bitmap.Height)
    $offsetX = [int](($bitmap.Width - $squareSize) / 2)
    $offsetY = [int](($bitmap.Height - $squareSize) / 2)

    $cropRect = New-Object System.Drawing.Rectangle($offsetX, $offsetY, $squareSize, $squareSize)
    $squareBitmap = New-Object System.Drawing.Bitmap($squareSize, $squareSize)

    try {
        $graphics = [System.Drawing.Graphics]::FromImage($squareBitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($bitmap, 0, 0, $cropRect, [System.Drawing.GraphicsUnit]::Pixel)
        } finally {
            $graphics.Dispose()
        }

        $outputDirectory = Split-Path -Parent $OutputImage
        if ($outputDirectory) {
            New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
        }

        $squareBitmap.Save($OutputImage, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $squareBitmap.Dispose()
    }
} finally {
    $bitmap.Dispose()
}
