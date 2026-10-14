[CmdletBinding()]
param([ValidateSet('Debug','Release')][string] $Configuration = 'Debug')
. "$PSScriptRoot\common.ps1"
$executable = Join-Path $ProjectRoot "src\FrameTrail.App\bin\x64\$Configuration\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe"
if (!(Test-Path $executable)) {
    $executable = Join-Path $ProjectRoot "src\FrameTrail.App\bin\$Configuration\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe"
}
if (!(Test-Path $executable)) { throw 'Application is not built. Run scripts/build.ps1 first.' }
# Launch the user-facing application on explicit run; helper processes use hidden windows.
Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable)
