[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$app = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\FrameTrail.App'
$brand = Join-Path $app 'Assets\Brand'
$destination = Join-Path $app 'PackageAssets'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Bitmap]::new((Join-Path $brand 'FrameTrail.Icon.v1.png'))

function Save-IconPng([string] $Name, [int] $Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $Size, $Size), 0, 0, $source.Width, $source.Height, [Drawing.GraphicsUnit]::Pixel)
        $bitmap.Save((Join-Path $destination $Name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

try {
    foreach ($entry in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
        Save-IconPng ($entry[0] + '.png') $entry[1]
        foreach ($scale in @(125, 150, 200, 400)) {
            Save-IconPng ($entry[0] + '.scale-' + $scale + '.png') ([int][Math]::Round($entry[1] * $scale / 100.0, [MidpointRounding]::AwayFromZero))
        }
    }
    foreach ($size in @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)) {
        $name = 'Square44x44Logo.targetsize-' + $size
        Save-IconPng ($name + '.png') $size
        foreach ($form in @('unplated', 'lightunplated')) {
            Copy-Item -LiteralPath (Join-Path $destination ($name + '.png')) -Destination (Join-Path $destination ($name + '_altform-' + $form + '.png')) -Force
        }
    }
} finally { $source.Dispose() }
Write-Output 'Generated 57 package icons from FrameTrail.Icon.v1.png, preserving transparency.'
