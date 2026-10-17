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

function Import-FrameTrailCompilerEnvironment([string] $Setup, [string] $Arguments = '') {
    # CALL keeps the quoted path away from cmd's special leading-quote parsing.
    # Arguments are fixed compiler-setup flags supplied by the callers below.
    $lines = & $env:ComSpec /d /s /c "call `"$Setup`" $Arguments >nul && set"
    if ($LASTEXITCODE -ne 0) { throw "Native toolchain environment setup failed: $Setup (exit $LASTEXITCODE)." }
    foreach ($line in $lines) {
        if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
    }
}

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
        Import-FrameTrailCompilerEnvironment $setup
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

function Import-FrameTrailMsvc {
    # Preserve a portable toolchain or an existing developer-shell environment.
    if (Get-Command cl.exe -ErrorAction SilentlyContinue) { return }
    $vswhere = Resolve-FrameTrailTool 'vswhere.exe' @("${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe")
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0) { throw "Visual Studio discovery failed with code $LASTEXITCODE." }
    if (!$installation) { throw 'No MSVC x64 toolchain found. Install the Visual Studio C++ desktop workload or configure -ToolchainRoot; see docs/toolchain.md.' }
    $setup = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
    if (!(Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Visual Studio environment setup was not found: $setup" }
    Write-Host "Loading MSVC x64 from $installation"
    Import-FrameTrailCompilerEnvironment $setup '-no_logo -arch=x64 -host_arch=x64'
    # VS ships these alongside the C++ workload even when they are not on PATH.
    foreach ($folder in @('Ninja', 'CMake\bin')) {
        $candidate = Join-Path $installation "Common7\IDE\CommonExtensions\Microsoft\CMake\$folder"
        if (Test-Path -LiteralPath $candidate -PathType Container) { $env:PATH += [IO.Path]::PathSeparator + $candidate }
    }
    if (!(Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw 'Visual Studio setup did not expose the MSVC compiler.' }
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

function Get-FrameTrailPackagingTools {
    $candidates = @()
    foreach ($root in @((Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'), "${env:ProgramFiles(x86)}\Windows Kits\10\bin", (Join-Path $DefaultToolchainRoot 'msvc\Windows Kits\10\bin'))) {
        if (Test-Path -LiteralPath $root) { $candidates += Get-ChildItem -LiteralPath $root -Filter makeappx.exe -Recurse | Where-Object FullName -Match '[\\/]x64[\\/]' | Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName }
    }
    $makeappx = if ($candidates.Count) { $candidates[0] } else { Resolve-FrameTrailTool 'makeappx.exe' }
    [pscustomobject]@{ MakeAppx = $makeappx; MakePri = Resolve-FrameTrailTool 'makepri.exe' @((Join-Path (Split-Path $makeappx) 'makepri.exe')) }
}
