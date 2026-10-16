[CmdletBinding()]
param([string] $OutputDirectory, [string] $NativeBuildDirectory, [string] $ToolchainRoot)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\m4\recovery-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new validation directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$dotnet = Resolve-FrameTrailTool 'dotnet'
$smokeProject = Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\FrameTrail.Workspace.Smoke.csproj'
Invoke-Checked $dotnet @('build',$smokeProject,'-c','Release','-p:RestoreLockedMode=true')
$smoke = Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\bin\Release\net8.0\FrameTrail.Workspace.Smoke.exe'
$env:PATH = (Get-NativeBinaryDirectory $NativeBuildDirectory 'Release') + [IO.Path]::PathSeparator + $env:PATH
$project = Join-Path $OutputDirectory 'interrupted.frametrailproject'
$surface = $null; $recording = $null
try {
    $surface = Start-Process -FilePath (Join-Path (Get-NativeBinaryDirectory $NativeBuildDirectory 'Release') 'FrameTrail.Capture.Benchmark.exe') -ArgumentList @('--surface','30') -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 600
    $recording = Start-Process -FilePath $smoke -ArgumentList @(('"' + $project + '"'),'--fixture','--checkpoint','--fps60') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputDirectory 'recording.log') -RedirectStandardError (Join-Path $OutputDirectory 'recording.stderr.txt')
    $deadline = [DateTime]::UtcNow.AddSeconds(20); $checkpoint = $null
    while ([DateTime]::UtcNow -lt $deadline -and !$recording.HasExited) {
        $file = Join-Path $project 'recovery\manifest.json'
        if (Test-Path -LiteralPath $file) {
            try { $candidate = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json; if ($candidate.checkpointJson) { $checkpoint = $candidate; break } } catch { }
        }
        Start-Sleep -Milliseconds 100
    }
    if (!$checkpoint) { throw 'No finalized checkpoint observed before timeout.' }
    $recording.Kill(); $recording.WaitForExit()
    $before = @($checkpoint.pendingFiles)
    Invoke-Checked $smoke @($project,'--recover')
    $recovery = Get-Content -LiteralPath ($project + '.recovery.result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$recovery.success -or !$recovery.reopened -or $recovery.segmentCount -lt 1) { throw 'Terminated recording could not be recovered.' }
    foreach ($pending in $before) {
        if (!(Test-Path -LiteralPath (Join-Path $project $pending))) { throw 'Recovery unexpectedly removed unfinished source media.' }
    }
    [ordered]@{ success = $true; processTerminated = $true; recoveredSegments = $recovery.segmentCount; pendingFilesPreserved = $before.Count;
        reopened = $recovery.reopened; source = 'own animated fixture; no desktop, microphone or camera'; timestampUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding UTF8
} finally {
    foreach ($child in @($recording,$surface)) { if ($child) { if (!$child.HasExited) { $child.Kill(); $child.WaitForExit() }; $child.Dispose() } }
}
