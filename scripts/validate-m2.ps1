[CmdletBinding()]
param([string] $OutputDirectory, [string] $NativeBuildDirectory, [string] $ToolchainRoot, [switch] $Matrix)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native-release' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\m2\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new validation directory; existing runs are retained.' }
$fixture = Join-Path $OutputDirectory 'fixture'
New-Item -ItemType Directory -Path (Join-Path $fixture 'media') -Force | Out-Null
$ffmpeg = Resolve-FrameTrailTool 'ffmpeg'
$null = Resolve-FrameTrailTool 'ffprobe'
$python = Resolve-FrameTrailTool 'python'
$dotnet = Resolve-FrameTrailTool 'dotnet'
Invoke-Checked $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=3','-c:v','libx264','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709',(Join-Path $fixture 'media\screen.mp4'))
Invoke-Checked $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','testsrc2=size=1920x1080:rate=30:duration=3','-c:v','libx264','-preset','ultrafast','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709',(Join-Path $fixture 'media\screen1080.mp4'))
Invoke-Checked $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','color=c=0x55786c:size=640x360:rate=1:duration=1','-c:v','libx264','-pix_fmt','yuv420p',(Join-Path $fixture 'media\screen-held.mp4'))
Invoke-Checked $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','color=c=0x35aa66:size=320x240:rate=30:duration=2','-c:v','libx264','-pix_fmt','yuv420p',(Join-Path $fixture 'media\camera.mp4'))
Invoke-Checked $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=3','-ac','2','-c:a','pcm_s16le',(Join-Path $fixture 'media\system.wav'))
Invoke-Checked $ffmpeg @('-hide_banner','-loglevel','error','-f','lavfi','-i','sine=frequency=880:sample_rate=48000:duration=3','-ac','2','-c:a','pcm_s16le',(Join-Path $fixture 'media\microphone.wav'))
$originalPath = $env:PATH
try {
    $env:PATH = (Get-NativeBinaryDirectory $NativeBuildDirectory 'Release') + [IO.Path]::PathSeparator + $env:PATH
    $arguments = @('run','--project',(Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke'),'-c','Release','--','--m2',$fixture,$OutputDirectory)
    if ($Matrix) { $arguments += '--matrix' }
    Invoke-Checked $dotnet $arguments
    Invoke-Checked $python @((Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\verify_m2_media.py'),$OutputDirectory,$fixture)
} finally { $env:PATH = $originalPath }
