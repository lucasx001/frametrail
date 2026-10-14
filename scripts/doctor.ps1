[CmdletBinding()]
param([string] $ToolchainRoot)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
$os = Get-CimInstance Win32_OperatingSystem
Write-Output "OS: $($os.Caption), build $($os.BuildNumber); architecture $env:PROCESSOR_ARCHITECTURE"
Write-Output "PowerShell: $($PSVersionTable.PSVersion)"
foreach ($tool in @('dotnet','cmake','ninja','cl','msbuild')) {
    $command = Get-Command $tool -ErrorAction SilentlyContinue
    Write-Output ("{0}: {1}" -f $tool, $(if($command){$command.Source}else{'not in PATH'}))
}
if (Get-Command dotnet -ErrorAction SilentlyContinue) { dotnet --list-sdks }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if(Test-Path $vswhere) { & $vswhere -products '*' -format json }
Write-Output "Windows SDK include directories:"
Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\Include" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
