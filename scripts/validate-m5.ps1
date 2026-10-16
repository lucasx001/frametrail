[CmdletBinding()]
param([string] $OutputDirectory, [string] $NativeBuildDirectory)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain ''
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native-release' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\validation\m5\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new directory; validation evidence is retained.' }
$fixture = Join-Path $OutputDirectory 'fixture'
$dotnet = Resolve-FrameTrailTool 'dotnet'; $python = Resolve-FrameTrailTool 'python'
Invoke-Checked $python @((Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\prepare_android_fixture.py'), $fixture)
Invoke-Checked $python @((Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\prepare_android_fixture.py'), (Join-Path $fixture 'portrait'), '360x800')
$originalPath = $env:PATH
try {
    $env:PATH = (Get-NativeBinaryDirectory $NativeBuildDirectory 'Release') + [IO.Path]::PathSeparator + $env:PATH
    Invoke-Checked $dotnet @('run','--project',(Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke'),'-c','Release','--','--m5',$fixture,$OutputDirectory,(Join-Path $ProjectRoot 'artifacts\android-runtime'))
    $crashDirectory = Join-Path $OutputDirectory 'process-crash'
    New-Item -ItemType Directory -Path $crashDirectory | Out-Null
    $harness = Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\bin\Release\net8.0\FrameTrail.Workspace.Smoke.dll'
    $crashArguments = @(('"' + $harness + '"'),'--m5',('"' + $fixture + '"'),('"' + $crashDirectory + '"'),('"' + (Join-Path $ProjectRoot 'artifacts\android-runtime') + '"'),'--crash')
    $crashProcess = Start-Process -FilePath $dotnet -ArgumentList $crashArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $crashDirectory 'stdout.log') -RedirectStandardError (Join-Path $crashDirectory 'stderr.log')
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (!(Test-Path -LiteralPath (Join-Path $crashDirectory 'crash-ready.txt'))) {
            if ($crashProcess.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Crash helper did not produce its finalized checkpoint.' }
            Start-Sleep -Milliseconds 200
        }
        # Terminate only the new helper process created above. Pending MP4 is intentionally unfinalized.
        $crashProcess.Kill(); $crashProcess.WaitForExit()
    } finally { if (!$crashProcess.HasExited) { $crashProcess.Kill(); $crashProcess.WaitForExit() }; $crashProcess.Dispose() }
    Invoke-Checked $dotnet @($harness,(Join-Path $crashDirectory 'phone.frametrailproject'),'--recover')
    Invoke-Checked $python @((Join-Path $ProjectRoot 'tests\FrameTrail.Workspace.Smoke\verify_android_media.py'), $OutputDirectory)
} finally { $env:PATH = $originalPath }
