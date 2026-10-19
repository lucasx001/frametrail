[CmdletBinding()]
param([Parameter(Mandatory)][string] $ResultDirectory, [Parameter(Mandatory)][string] $AssetDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function Read-SampleBitmap([string] $Path, [bool] $Cover) {
    $inputImage = [Drawing.Image]::FromFile($Path)
    $small = New-Object Drawing.Bitmap 320,180
    $graphics = [Drawing.Graphics]::FromImage($small)
    try {
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $width = [double]$inputImage.Width; $height = [double]$inputImage.Height
        if ($Cover) { $scale = [Math]::Max(320 / $width, 180 / $height); $cropWidth = 320 / $scale; $cropHeight = 180 / $scale }
        else { $cropWidth = $width; $cropHeight = $height }
        $crop = New-Object Drawing.RectangleF (($width - $cropWidth) / 2),(($height - $cropHeight) / 2),$cropWidth,$cropHeight
        $destination = New-Object Drawing.RectangleF 0,0,320,180
        $graphics.DrawImage($inputImage, $destination, $crop, [Drawing.GraphicsUnit]::Pixel)
        return $small
    } catch { $small.Dispose(); throw }
    finally { $graphics.Dispose(); $inputImage.Dispose() }
}
function Measure-BackgroundError([Drawing.Bitmap] $Expected, [Drawing.Bitmap] $Actual) {
    $total = 0.0; $count = 0
    foreach ($point in @(@(13,7),@(160,7),@(307,7),@(13,90),@(307,90),@(13,173),@(160,173),@(307,173))) {
        foreach ($dx in -1..1) { foreach ($dy in -1..1) {
            $a = $Expected.GetPixel($point[0] + $dx, $point[1] + $dy); $b = $Actual.GetPixel($point[0] + $dx, $point[1] + $dy)
            $total += [Math]::Abs([int]$a.R - [int]$b.R) + [Math]::Abs([int]$a.G - [int]$b.G) + [Math]::Abs([int]$a.B - [int]$b.B); $count += 3
        } }
    }
    return $total / $count
}
$checks = @()
foreach ($number in 1..18) {
    $expected = Read-SampleBitmap (Join-Path $AssetDirectory "wallpaper$number.jpg") $true
    $actual = Read-SampleBitmap (Join-Path $ResultDirectory "wallpaper-$number.png") $false
    try { $difference = Measure-BackgroundError $expected $actual; if ($difference -gt 12) { throw "Preview does not match wallpaper $number (RGB mean error $difference)." }; $checks += @{ wallpaper = $number; meanRgbError = $difference } }
    finally { $expected.Dispose(); $actual.Dispose() }
}
$ffmpeg = (Get-Command ffmpeg.exe -ErrorAction Stop).Source
$exportFrame = Join-Path $ResultDirectory 'export-first-frame.png'
& $ffmpeg -v error -y -i (Join-Path $ResultDirectory 'wallpaper-export.mp4') -frames:v 1 $exportFrame
if ($LASTEXITCODE -ne 0) { throw 'Export frame decode failed.' }
$expected = Read-SampleBitmap (Join-Path $AssetDirectory 'wallpaper3.jpg') $true
$actual = Read-SampleBitmap $exportFrame $false
try { $difference = Measure-BackgroundError $expected $actual; if ($difference -gt 12) { throw "Export does not match the selected wallpaper (RGB mean error $difference)." } }
finally { $expected.Dispose(); $actual.Dispose() }
@{ success = $true; previewChecks = $checks; exportMeanRgbError = $difference; note = 'Eight exterior patches independently compared to centered upstream JPEGs; RGB mean error tolerance 12/255.' } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ResultDirectory 'wallpaper-pixels.json') -Encoding UTF8
Write-Host 'All 18 previews and the decoded export match their wallpaper pixels.'
