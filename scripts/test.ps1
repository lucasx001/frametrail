[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string] $Configuration = 'Debug',
    [string] $NativeBuildDirectory,
    [string] $ToolchainRoot,
    [switch] $SkipNative,
    [switch] $IncludeHardware,
    [switch] $LockedRestore
)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native' }
if (!$SkipNative) {
    $ctest = Resolve-FrameTrailTool 'ctest' @("$ProjectRoot\.tools\cmake\bin\ctest.exe")
    Invoke-Checked $ctest @('--test-dir', $NativeBuildDirectory, '-C', $Configuration, '--output-on-failure')
}
$dotnet = Resolve-FrameTrailTool 'dotnet' @("$ProjectRoot\.tools\dotnet\dotnet.exe")
$originalPath = $env:PATH
try {
    $env:PATH = (Get-NativeBinaryDirectory $NativeBuildDirectory $Configuration) + [IO.Path]::PathSeparator + $originalPath
    foreach ($testProject in Get-ChildItem (Join-Path $ProjectRoot 'tests') -Filter '*.Tests.csproj' -Recurse) {
        $arguments = @('test', $testProject.FullName, '-c', $Configuration, '--logger', ('trx;LogFileName=' + $testProject.BaseName + '.trx'), '--results-directory', (Join-Path $ProjectRoot 'artifacts\test-results'))
        if (!$IncludeHardware) { $arguments += @('--filter', 'Category!=Hardware') }
        if ($LockedRestore) { $arguments += '-p:RestoreLockedMode=true' }
        Invoke-Checked $dotnet $arguments
    }
} finally { $env:PATH = $originalPath }
