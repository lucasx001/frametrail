[CmdletBinding()]
param([string] $OutputDirectory, [string] $NativeBuildDirectory, [string] $ToolchainRoot)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native-release' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\m3\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new validation directory; existing runs are retained.' }
$fixture = Join-Path $OutputDirectory 'fixture'
New-Item -ItemType Directory -Path (Join-Path $fixture 'media'), (Join-Path $fixture 'assets') -Force | Out-Null
$ffmpeg = Resolve-FrameTrailTool 'ffmpeg'
$null = Resolve-FrameTrailTool 'ffprobe'
$python = Resolve-FrameTrailTool 'python'
$dotnet = Resolve-FrameTrailTool 'dotnet'
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=3','-c:v','libx264','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709',(Join-Path $fixture 'media\screen.mp4'))
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','color=c=0x35aa66:size=320x240:rate=30:duration=2','-c:v','libx264','-pix_fmt','yuv420p',(Join-Path $fixture 'media\camera.mp4'))
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','color=c=red@0.5:s=96x64:d=1,format=rgba','-frames:v','1',(Join-Path $fixture 'assets\sticker.png'))
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=1','-frames:v','1',(Join-Path $fixture 'assets\background.png'))
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=1','-c:v','libx264','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709',(Join-Path $fixture 'assets\background.mp4'))
$originalPath = $env:PATH
try {
    $env:PATH = (Get-NativeBinaryDirectory $NativeBuildDirectory 'Release') + [IO.Path]::PathSeparator + $env:PATH
    Invoke-Checked $dotnet @('run','--project',(Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke'),'-c','Release','--','--m3',$fixture,(Join-Path $OutputDirectory 'result'))
    Invoke-Checked $python @((Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\verify_m3_media.py'),(Join-Path $OutputDirectory 'result'))
} finally { $env:PATH = $originalPath }
