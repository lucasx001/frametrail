[CmdletBinding()]
param()
. "$PSScriptRoot\common.ps1"
$fixture = Join-Path $ProjectRoot ('artifacts\test-results\compiler path ' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$setup = Join-Path $fixture 'setup.bat'
$failure = Join-Path $fixture 'failure.bat'
$probe = 'FRAMETRAIL_COMPILER_ENV_TEST'
$previous = [Environment]::GetEnvironmentVariable($probe, 'Process')
try {
    @('@echo off', "set $probe=portable=value") | Set-Content -LiteralPath $setup -Encoding ascii
    Import-FrameTrailCompilerEnvironment $setup
    if ([Environment]::GetEnvironmentVariable($probe, 'Process') -ne 'portable=value') { throw 'Portable setup with spaces or an equals sign failed.' }

    @('@echo off', 'if not "%*"=="-no_logo -arch=x64 -host_arch=x64" exit /b 10', "set $probe=visual-studio-x64") | Set-Content -LiteralPath $setup -Encoding ascii
    Import-FrameTrailCompilerEnvironment $setup '-no_logo -arch=x64 -host_arch=x64'
    if ([Environment]::GetEnvironmentVariable($probe, 'Process') -ne 'visual-studio-x64') { throw 'Visual Studio setup flags or path quoting failed.' }

    @('@echo off', "set $probe=must-not-be-imported", 'exit /b 7') | Set-Content -LiteralPath $failure -Encoding ascii
    $rejected = $false
    try { Import-FrameTrailCompilerEnvironment $failure } catch {
        if ($_.Exception.Message -notlike '*exit 7*') { throw }
        $rejected = $true
    }
    if (!$rejected -or [Environment]::GetEnvironmentVariable($probe, 'Process') -ne 'visual-studio-x64') { throw 'Failed compiler setup was accepted or imported.' }
    # A handled failure must not leak into the Actions shell exit check.
    $global:LASTEXITCODE = 0
    Write-Output "Compiler environment checks passed (PowerShell $($PSVersionTable.PSVersion))."
} finally { [Environment]::SetEnvironmentVariable($probe, $previous, 'Process') }
