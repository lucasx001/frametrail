[CmdletBinding()]
param(
    [ValidateRange(2,3600)][int] $DurationSeconds = 30,
    [string] $OutputDirectory,
    [string] $NativeBuildDirectory,
    [string] $ToolchainRoot
)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\m4\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new validation directory; previous evidence is retained.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$benchmark = Join-Path (Get-NativeBinaryDirectory $NativeBuildDirectory 'Release') 'FrameTrail.Capture.Benchmark.exe'
if (!(Test-Path -LiteralPath $benchmark)) { throw 'Build Release native binaries first.' }
$ffprobe = Resolve-FrameTrailTool 'ffprobe'; $ffmpeg = Resolve-FrameTrailTool 'ffmpeg'
$hardware = [ordered]@{ timestampUtc = [DateTime]::UtcNow.ToString('o'); osVersion = [Environment]::OSVersion.Version.ToString();
    cpu = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors);
    gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate);
    durationSeconds = $DurationSeconds; source = 'own animated 1920x1080 window'; audioSync = 'not-measured'; hardwareMatrix = 'this-machine-only' }
$hardware | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'environment.json') -Encoding UTF8
$video = Join-Path $OutputDirectory 'capture.mp4'; $stdout = Join-Path $OutputDirectory 'capture.json'; $stderr = Join-Path $OutputDirectory 'capture.stderr.txt'
$process = New-Object Diagnostics.Process
$process.StartInfo = New-Object Diagnostics.ProcessStartInfo
$process.StartInfo.FileName = $benchmark; $process.StartInfo.Arguments = "$DurationSeconds `"$video`""
$process.StartInfo.UseShellExecute = $false; $process.StartInfo.CreateNoWindow = $true
$process.StartInfo.RedirectStandardOutput = $true; $process.StartInfo.RedirectStandardError = $true
$null = $process.Start(); $outputTask = $process.StandardOutput.ReadToEndAsync(); $errorTask = $process.StandardError.ReadToEndAsync()
$samples = [Collections.Generic.List[object]]::new(); $clock = [Diagnostics.Stopwatch]::StartNew()
try {
    while (!$process.WaitForExit(1000)) {
        $process.Refresh()
        if (!$process.HasExited) { $samples.Add([ordered]@{ seconds = $clock.Elapsed.TotalSeconds; privateBytes = $process.PrivateMemorySize64; workingSetBytes = $process.WorkingSet64; handles = $process.HandleCount; threads = $process.Threads.Count }) }
        if ($clock.Elapsed.TotalSeconds -gt $DurationSeconds + 120) { throw 'Capture timed out.' }
    }
    $process.WaitForExit()
    $outputTask.Result | Set-Content -LiteralPath $stdout -Encoding UTF8
    $errorTask.Result | Set-Content -LiteralPath $stderr -Encoding UTF8
    if ($process.ExitCode -ne 0) { throw "Capture benchmark failed: see $stderr" }
    $capture = Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
    if ($capture.width -ne 1920 -or $capture.height -ne 1080 -or $capture.written -lt 1 -or $capture.durationSeconds -lt ($DurationSeconds - 1)) { throw 'Capture dimensions, frames or duration did not satisfy the run.' }
    $probeJson = & $ffprobe -v error -select_streams v:0 -count_frames -show_entries stream=width,height,nb_read_frames,duration,r_frame_rate -of json $video
    if ($LASTEXITCODE -ne 0) { throw 'ffprobe rejected the recording.' }
    $probeJson | Set-Content -LiteralPath (Join-Path $OutputDirectory 'probe.json') -Encoding UTF8
    $probe = ($probeJson | ConvertFrom-Json).streams[0]
    if ([long]$probe.nb_read_frames -ne [long]$capture.written) { throw 'Decoded frame count differs from capture writer count.' }
    Invoke-Checked $ffmpeg @('-v','error','-i',$video,'-map','0:v:0','-f','null','-')
    [ordered]@{ success = $true; scope = 'video-capture-only'; durationSeconds = $capture.durationSeconds; captureFps = $capture.fps;
        framesWritten = $capture.written; decodedFrames = [long]$probe.nb_read_frames; droppedFrames = $capture.dropped; queueHighWater = $capture.queueHigh;
        sampledResourcePoints = $samples.Count; longVideoRun = ($DurationSeconds -ge 1800); audioSyncAccepted = $false;
        resources = @($samples.ToArray()); timestampUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
    Write-Host "M4 capture evidence: $OutputDirectory. A/V synchronization and cross-hardware acceptance remain separate."
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $samples.ToArray() | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'resources.json') -Encoding UTF8
    $process.Dispose()
}
