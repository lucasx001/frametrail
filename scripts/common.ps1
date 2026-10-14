Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ProjectRoot = Split-Path $PSScriptRoot -Parent
$script:DefaultToolchainRoot = Join-Path $env:USERPROFILE 'AppData\Local\FrameTrail\Toolchain'

function Import-FrameTrailToolchain([string] $ToolchainRoot) {
    if (!$ToolchainRoot) { $ToolchainRoot = $script:DefaultToolchainRoot }
    if (!(Test-Path -LiteralPath $ToolchainRoot)) { return }
    $setup = Join-Path $ToolchainRoot 'msvc\setup_x64.bat'
    if (Test-Path -LiteralPath $setup) {
        # Read the environment from the compiler setup process; no global PATH mutation.
        $lines = & $env:ComSpec /d /s /c "`"`"$setup`" >nul && set`""
        if ($LASTEXITCODE -ne 0) { throw 'Native toolchain environment setup failed.' }
        foreach ($line in $lines) {
            if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
        }
    }
    foreach ($folder in @('python-packages\bin','python-packages\ninja\data\bin','python-packages\cmake\data\bin','dotnet')) {
        $candidate = Join-Path $ToolchainRoot $folder
        if (Test-Path -LiteralPath $candidate) { $env:PATH = $candidate + [IO.Path]::PathSeparator + $env:PATH }
    }
    $dotnetRoot = Join-Path $ToolchainRoot 'dotnet'
    if (Test-Path (Join-Path $dotnetRoot 'dotnet.exe')) { $env:DOTNET_ROOT = $dotnetRoot }
}

function Resolve-FrameTrailTool([string] $Name, [string[]] $Candidates = @()) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in $Candidates) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
    throw "Required tool '$Name' was not found. See docs/toolchain.md."
}

function Invoke-Checked([string] $Tool, [string[]] $Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited with code $LASTEXITCODE" }
}

function Get-NativeBinaryDirectory([string] $BuildDirectory, [string] $Configuration) {
    $configured = Join-Path $BuildDirectory "bin\$Configuration"
    if (Test-Path (Join-Path $configured 'FrameTrail.Engine.dll')) { return $configured }
    return Join-Path $BuildDirectory 'bin'
}
