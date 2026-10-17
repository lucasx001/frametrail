[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $PackageDirectory, [string] $InstallerCompiler)
. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\release-common.ps1"
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$release = ConvertTo-FrameTrailReleaseVersion (([version]$manifest.version).ToString(3))
$payload = Join-Path $PackageDirectory 'app'
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'FrameTrail.App.dll')).Version.ToString() -ne $release.FileVersion) { throw 'Installer payload version mismatch.' }
if (!$InstallerCompiler) { $InstallerCompiler = Resolve-FrameTrailTool 'ISCC.exe' @((Join-Path $ProjectRoot '.tools\inno-7.1.0\ISCC.exe')) }
Invoke-Checked $InstallerCompiler @('/Qp', "/DPayloadDirectory=$payload", "/DReleaseVersion=$($release.Version)", "/DFileVersion=$($release.FileVersion)", "/DPackageOutput=$PackageDirectory", (Join-Path $ProjectRoot 'packaging\FrameTrail.iss'))
$installer = Join-Path $PackageDirectory "FrameTrail-$($release.Version)-x64-Setup.exe"
if (!(Test-Path -LiteralPath $installer) -or (Get-Item -LiteralPath $installer).VersionInfo.FileVersion.Trim() -ne $release.FileVersion) { throw 'Installer executable or version metadata is missing.' }
Write-Output "Installer: $installer (unsigned, current user, Windows 11 x64)."
