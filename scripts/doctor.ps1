[CmdletBinding()]
param([string] $ToolchainRoot)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
$os = Get-CimInstance Win32_OperatingSystem
Write-Output "OS: $($os.Caption), build $($os.BuildNumber); architecture $env:PROCESSOR_ARCHITECTURE"
Write-Output "PowerShell: $($PSVersionTable.PSVersion)"
Write-Output "Project: $ProjectRoot"
Write-Output "USERPROFILE: $env:USERPROFILE"
Write-Output "LOCALAPPDATA: $env:LOCALAPPDATA"
Write-Output "Portable toolchain: $script:ActiveToolchainRoot"
foreach ($root in @($script:ActiveToolchainRoot) + @($script:ToolchainRootCandidates) | Select-Object -Unique) {
    if ($root) { Write-Output ("  directory exists: {0} - {1}" -f (Test-Path -LiteralPath $root -PathType Container), $root) }
}
foreach ($file in @('msvc\setup_x64.bat','python-packages\cmake\data\bin\cmake.exe','python-packages\bin\ninja.exe','python-packages\ninja\data\bin\ninja.exe','dotnet\dotnet.exe')) {
    if ($script:ActiveToolchainRoot) {
        $candidate = Join-Path $script:ActiveToolchainRoot $file
        Write-Output ("  file exists: {0} - {1}" -f (Test-Path -LiteralPath $candidate -PathType Leaf), $candidate)
    }
}
foreach ($tool in @('dotnet','cmake','ninja','cl','msbuild')) {
    $command = Get-Command $tool -ErrorAction SilentlyContinue
    Write-Output ("{0}: {1}" -f $tool, $(if($command){$command.Source}else{'not in PATH'}))
}
if (Get-Command dotnet -ErrorAction SilentlyContinue) { dotnet --list-sdks }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if(Test-Path $vswhere) { & $vswhere -products '*' -format json }
Write-Output "Windows SDK include directories:"
Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
