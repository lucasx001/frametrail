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
if (!$Generator) { $Generator = if (Get-Command cl -ErrorAction SilentlyContinue) { 'Ninja' } else { 'Visual Studio 17 2022' } }
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native' }
$cmake = Resolve-FrameTrailTool 'cmake' @("$ProjectRoot\.tools\cmake\bin\cmake.exe")
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
