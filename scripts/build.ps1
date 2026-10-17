[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string] $Configuration = 'Debug',
    [string] $Generator,
    [string] $ToolchainRoot,
    [string] $NativeBuildDirectory,
    [switch] $NativeOnly,
    [switch] $SkipTests,
    [switch] $LockedRestore
)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native' }
if (!$Generator) {
    # Keep existing build directories usable; fresh builds use version-independent Ninja.
    $cache = Join-Path $NativeBuildDirectory 'CMakeCache.txt'
    if (Test-Path -LiteralPath $cache) {
        $entry = Select-String -LiteralPath $cache -Pattern '^CMAKE_GENERATOR:INTERNAL=(.+)$' | Select-Object -First 1
        if ($entry) { $Generator = $entry.Matches[0].Groups[1].Value }
    }
    if (!$Generator) { $Generator = 'Ninja' }
}
if ($Generator -notlike 'Visual Studio*') { Import-FrameTrailMsvc }
if ($Generator -like 'Ninja*') { $null = Resolve-FrameTrailTool 'ninja' }
$cmake = Resolve-FrameTrailTool 'cmake' @("$ProjectRoot\.tools\cmake\bin\cmake.exe")
Write-Host "Native generator: $Generator; CMake: $cmake"
if ($compiler = Get-Command cl.exe -ErrorAction SilentlyContinue) { Write-Host "Native compiler: $($compiler.Source)" }
$configureArgs = @('-S', $ProjectRoot, '-B', $NativeBuildDirectory, '-G', $Generator, '-DBUILD_TESTING=ON')
if ($Generator -like 'Visual Studio*') { $configureArgs += @('-A', 'x64') }
else { $configureArgs += "-DCMAKE_BUILD_TYPE=$Configuration" }
Invoke-Checked $cmake $configureArgs
Invoke-Checked $cmake @('--build', $NativeBuildDirectory, '--config', $Configuration, '--parallel')
if (!$SkipTests) {
    $ctest = Resolve-FrameTrailTool 'ctest'
    Invoke-Checked $ctest @('--test-dir', $NativeBuildDirectory, '-C', $Configuration, '--output-on-failure')
}
if ($NativeOnly) { return }
$dotnet = Resolve-FrameTrailTool 'dotnet' @("$ProjectRoot\.tools\dotnet\dotnet.exe")
$app = Join-Path $ProjectRoot 'src\FrameTrail.App\FrameTrail.App.csproj'
$restoreArgs = @('restore', $app, '-p:Platform=x64')
if ($LockedRestore) { $restoreArgs += '--locked-mode' }
Invoke-Checked $dotnet $restoreArgs
Invoke-Checked $dotnet @('build', $app, '--no-restore', '-c', $Configuration, '-p:Platform=x64', "-p:NativeBuildDirectory=$NativeBuildDirectory")
if (!$SkipTests) { & "$PSScriptRoot\test.ps1" -Configuration $Configuration -NativeBuildDirectory $NativeBuildDirectory -ToolchainRoot $ToolchainRoot -SkipNative -LockedRestore:$LockedRestore }
