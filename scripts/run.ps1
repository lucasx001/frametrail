[CmdletBinding()]
param([ValidateSet('Debug','Release')][string] $Configuration = 'Debug', [string] $ProjectPath)
. "$PSScriptRoot\common.ps1"
$runtime = Join-Path $script:DefaultToolchainRoot 'dotnet'
if (Test-Path (Join-Path $runtime 'dotnet.exe')) { $env:DOTNET_ROOT = $runtime }
$executable = Join-Path $ProjectRoot "src\FrameTrail.App\bin\x64\$Configuration\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe"
if (!(Test-Path $executable)) {
    $executable = Join-Path $ProjectRoot "src\FrameTrail.App\bin\$Configuration\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe"
}
if (!(Test-Path $executable)) { throw 'Application is not built. Run scripts/build.ps1 first.' }
# Launch the user-facing application on explicit run; helper processes use hidden windows.
if ($ProjectPath) {
    $ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
    if (Test-Path -LiteralPath $ProjectPath -PathType Leaf) {
        if ([IO.Path]::GetFileName($ProjectPath) -ne 'project.json') { throw 'Choose a project directory or its project.json.' }
        $ProjectPath = Split-Path $ProjectPath
    }
    Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -ArgumentList @('--editor', ('"' + $ProjectPath + '"'))
} else { Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) }
