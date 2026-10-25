[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $NativeBuildDirectory,
    [string] $ToolchainRoot,
    [switch] $SkipBuild,
    [ValidateRange(30,600)][int] $TimeoutSeconds = 180
)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native-release' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\workflows\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; previous workflow evidence is retained.' }
$dotnet = Resolve-FrameTrailTool 'dotnet'
$ffprobe = Resolve-FrameTrailTool 'ffprobe'
$ffmpeg = Resolve-FrameTrailTool 'ffmpeg'
if (!$SkipBuild) {
    Invoke-Checked $dotnet @('build', (Join-Path $ProjectRoot 'src\FrameTrail.App\FrameTrail.App.csproj'), '-c', 'Release', '-p:Platform=x64', "-p:NativeBuildDirectory=$NativeBuildDirectory", '-p:RestoreLockedMode=true')
}
$app = Join-Path $ProjectRoot 'src\FrameTrail.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe'
if (!(Test-Path -LiteralPath $app)) { throw 'Build the Release app before using -SkipBuild.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$applicationOutput = Join-Path $OutputDirectory 'application'
$success = $false
$failure = $null
$report = $null
$process = $null
try {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $app
    $start.WorkingDirectory = Split-Path $app
    $start.Arguments = '--inspect-ui --language zh-Hans --workflow-self-test "' + $applicationOutput + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $script:ActiveToolchainRoot 'dotnet'
    $process = [Diagnostics.Process]::Start($start)
    if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill(); $process.WaitForExit()
        throw 'Only the launched test app was stopped after the workflow timeout; inspect partial application evidence.'
    }
    $resultPath = Join-Path $applicationOutput 'workflow-validation.json'
    if (!(Test-Path -LiteralPath $resultPath)) { throw "The test app did not produce a result (exit $($process.ExitCode))." }
    $report = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or !$report.success) { throw "Window self-test failed: $($report.failure)" }
    if (@($report.chains).Count -ne 3 -or $report.actualInputVerified) { throw 'Unexpected self-test evidence schema or input-verification claim.' }
    $output = Join-Path $applicationOutput 'recorded-edited.mp4'
    $probeText = & $ffprobe -v error -show_entries 'stream=codec_type,codec_name,width,height,nb_frames,duration' -of json $output
    if ($LASTEXITCODE -ne 0) { throw 'ffprobe rejected the exported recording.' }
    $probeText | Set-Content -LiteralPath (Join-Path $OutputDirectory 'export-probe.json') -Encoding UTF8
    $streams = ($probeText | ConvertFrom-Json).streams
    $video = @($streams | Where-Object { $_.codec_type -eq 'video' })
    $audio = @($streams | Where-Object { $_.codec_type -eq 'audio' })
    if ($video.Count -ne 1 -or $video[0].width -ne 640 -or $video[0].height -ne 360 -or [double]$video[0].duration -lt 1 -or $audio.Count -ne 1) { throw 'Export video dimensions, duration or synthetic audio track are missing.' }
    Invoke-Checked $ffmpeg @('-v','error','-i',$output,'-map','0:v:0','-map','0:a:0','-f','null','-')
    $success = $true
}
catch { $failure = $_.Exception.ToString(); throw }
finally {
    if ($process) { $process.Dispose() }
    [ordered]@{
        success = $success
        timestampUtc = [DateTime]::UtcNow.ToString('o')
        evidenceLevel = 'real-window application-method integration + independent media decode'
        applicationResult = 'application/workflow-validation.json'
        chainCount = $(if ($report) { @($report.chains).Count } else { 0 })
        actualInputVerified = $false
        actualInputStatus = 'pending separate keyboard/pointer walkthrough'
        failure = $failure
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'workflow-summary.json') -Encoding UTF8
}
Write-Output "Workflow application self-tests and independent decode passed: $OutputDirectory. Real input remains a separate acceptance step."
