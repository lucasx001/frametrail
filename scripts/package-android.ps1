[CmdletBinding()]
param([string] $OutputDirectory, [string] $NativeBuildDirectory)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain ''
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native-release' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\packages\FrameTrail-android-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -or (Test-Path -LiteralPath ($OutputDirectory + '.zip'))) { throw 'Choose a new package output directory.' }
if (!(Test-Path -LiteralPath (Join-Path $ProjectRoot 'artifacts\android-runtime\sha256.json'))) { throw 'Run scripts/setup-android.cmd first.' }
& "$PSScriptRoot\setup-android.ps1"
$dotnet = Resolve-FrameTrailTool 'dotnet'
Invoke-Checked $dotnet @('publish',(Join-Path $ProjectRoot 'src\FrameTrail.App\FrameTrail.App.csproj'),'-c','Release','-p:Platform=x64',"-p:NativeBuildDirectory=$NativeBuildDirectory",'--self-contained','true','-o',$OutputDirectory)
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $OutputDirectory 'ENGINE-THIRD-PARTY-NOTICES.md')
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory 'docs\licenses') | Out-Null
Copy-Item -Path (Join-Path $ProjectRoot 'docs\licenses\*') -Destination (Join-Path $OutputDirectory 'docs\licenses')
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory 'docs\validation') | Out-Null
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs\validation\0012-android-usb-validation.md') -Destination (Join-Path $OutputDirectory 'docs\validation')
foreach ($name in @('FrameTrail.Android.dll','FrameTrail.App.pri','android-runtime\adb.exe','android-runtime\scrcpy-server','android-runtime\ADB-NOTICE.txt','android-runtime\scrcpy-LICENSE.txt','hostfxr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory $name))) { throw "Package is missing $name" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory, ($OutputDirectory + '.zip'))
Write-Output ($OutputDirectory + '.zip')
