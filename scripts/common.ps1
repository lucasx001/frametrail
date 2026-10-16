Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ProjectRoot = Split-Path $PSScriptRoot -Parent
$script:ToolchainRootCandidates = @(
    Join-Path $script:ProjectRoot '.tools\toolchain'
    if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'FrameTrail\Toolchain' }
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    if ($localAppData) { Join-Path $localAppData 'FrameTrail\Toolchain' }
    if ($env:USERPROFILE) { Join-Path $env:USERPROFILE 'AppData\Local\FrameTrail\Toolchain' }
) | Select-Object -Unique
$script:DefaultToolchainRoot = $script:ToolchainRootCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
if (!$script:DefaultToolchainRoot) { $script:DefaultToolchainRoot = $script:ToolchainRootCandidates | Select-Object -First 1 }
$script:ActiveToolchainRoot = $script:DefaultToolchainRoot
$script:ToolchainBinFolders = @('python-packages\bin','python-packages\ninja\data\bin','python-packages\cmake\data\bin','dotnet')

function Import-FrameTrailToolchain([string] $ToolchainRoot) {
    if ($ToolchainRoot -and !(Test-Path -LiteralPath $ToolchainRoot -PathType Container)) {
        throw "Toolchain directory '$ToolchainRoot' does not exist or is not accessible. Check -ToolchainRoot; see docs/toolchain.md."
    }
    if (!$ToolchainRoot) { $ToolchainRoot = $script:DefaultToolchainRoot }
    $script:ActiveToolchainRoot = $ToolchainRoot
    if (!$ToolchainRoot -or !(Test-Path -LiteralPath $ToolchainRoot -PathType Container)) { return }
    $ToolchainRoot = (Resolve-Path -LiteralPath $ToolchainRoot).ProviderPath
    $script:ActiveToolchainRoot = $ToolchainRoot
    $setup = Join-Path $ToolchainRoot 'msvc\setup_x64.bat'
    if (Test-Path -LiteralPath $setup) {
        # Read the environment from the compiler setup process; no global PATH mutation.
        $lines = & $env:ComSpec /d /s /c "`"`"$setup`" >nul && set`""
        if ($LASTEXITCODE -ne 0) { throw 'Native toolchain environment setup failed.' }
        foreach ($line in $lines) {
            if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
        }
    }
    foreach ($folder in $script:ToolchainBinFolders) {
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
    throw "Required tool '$Name' was not found. Portable toolchain directory: '$script:ActiveToolchainRoot'. Run .\scripts\doctor.cmd to check directory and tool visibility. See docs/toolchain.md."
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

function Get-FrameTrailSha256([string] $Path) {
    $hash = [Security.Cryptography.SHA256]::Create(); $inputFile = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($hash.ComputeHash($inputFile)).Replace('-','').ToLowerInvariant() }
    finally { $inputFile.Dispose(); $hash.Dispose() }
}
