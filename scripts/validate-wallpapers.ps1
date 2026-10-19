[CmdletBinding()]
param([string] $OutputDirectory, [ValidateSet('zh-Hans','en')][string] $Language = 'zh-Hans', [string] $ToolchainRoot)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\wallpapers\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new validation directory; previous runs are retained.' }
$fixture = Join-Path $OutputDirectory 'Wallpaper-Demo.frametrailproject'
$result = Join-Path $OutputDirectory 'result'
New-Item -ItemType Directory -Path (Join-Path $fixture 'media'), $result -Force | Out-Null
$ffmpeg = Resolve-FrameTrailTool 'ffmpeg'
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','testsrc2=size=1280x720:rate=30:duration=2','-c:v','libx264','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709',(Join-Path $fixture 'media\screen.mp4'))
$project = Get-Content -LiteralPath (Join-Path $ProjectRoot 'tests\FrameTrail.Core.Tests\fixtures\project-v1.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$time = @{ ticks = 2; timescale = 1 }; $zero = @{ ticks = 0; timescale = 1 }
$project.projectId = 'wallpaper-fixture'; $project.revision = 1; $project.sourceDuration = $time
$project.media = @(@{ id = 'screen'; kind = 'screen'; path = 'media/screen.mp4'; duration = $time; startOffset = $zero; width = 1280; height = 720 })
$project.segments = @(@{ id = 'clip'; sourceStart = $zero; sourceDuration = $time; rate = @{ numerator = 1; denominator = 1 } })
$project.effects = @(); $project.markers = @(); $project.canvas.camera.enabled = $false; $project.canvas.padding = 0.12
$project.export.fps.numerator = 30
$project | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $fixture 'project.json') -Encoding UTF8
$application = Join-Path $ProjectRoot 'src\FrameTrail.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe'
$process = Start-Process -FilePath $application -ArgumentList @('--editor', ('"' + $fixture + '"'), '--wallpapers-self-test', ('"' + $result + '"'), '--language', $Language) -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(240000)) { throw 'Wallpaper validation timed out; inspect the running fixture editor.' }
$report = Join-Path $result 'wallpapers-ui.json'
if (!(Test-Path -LiteralPath $report)) { throw 'The editor did not produce a validation report.' }
$validation = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$validation.success) { throw $validation.failure }
& "$PSScriptRoot\verify-wallpaper-pixels.ps1" -ResultDirectory $result -AssetDirectory (Join-Path $ProjectRoot 'src\FrameTrail.App\Assets\Wallpapers')
Write-Host ('Wallpaper UI and media checks passed: ' + $report)
