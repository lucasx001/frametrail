[CmdletBinding()]
param([string] $OutputDirectory, [string] $NativeBuildDirectory, [string] $ToolchainRoot)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\editor-polish\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new validation directory.' }
$project = Join-Path $OutputDirectory 'fixture.frametrailproject'
New-Item -ItemType Directory -Path (Join-Path $project 'media'), (Join-Path $project 'events'), (Join-Path $project 'assets') -Force | Out-Null
$ffmpeg = Resolve-FrameTrailTool 'ffmpeg'
$python = Resolve-FrameTrailTool 'python'
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','testsrc2=size=640x360:rate=30:duration=4','-c:v','libx264','-pix_fmt','yuv420p','-colorspace','bt709','-color_primaries','bt709','-color_trc','bt709',(Join-Path $project 'media\screen.mp4'))
Invoke-Checked $ffmpeg @('-v','error','-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=4','-ac','2','-c:a','pcm_s16le',(Join-Path $project 'media\system.wav'))
Invoke-Checked $python @((Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\prepare_editor_polish.py'), $project)
$app = Join-Path $ProjectRoot 'src\FrameTrail.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe'
$env:DOTNET_ROOT = Join-Path $script:DefaultToolchainRoot 'dotnet'
$result = Join-Path $OutputDirectory 'ui'
$process = Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -WindowStyle Hidden -ArgumentList @('--editor', ('"' + $project + '"'), '--editor-polish-self-test', ('"' + $result + '"')) -PassThru
if (!$process.WaitForExit(240000)) { throw "Validation process still running: $($process.Id). Inspect its result directory." }
$report = Get-Content (Join-Path $result 'editor-polish-validation.json') -Raw | ConvertFrom-Json
if (!$report.success) { throw $report.failure }
Invoke-Checked $python @((Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\verify_editor_polish.py'), $result)
Write-Output $OutputDirectory
