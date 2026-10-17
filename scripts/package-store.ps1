[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $PackageDirectory,
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [string] $PackageName,
    [string] $Publisher,
    [string] $PublisherDisplayName,
    [string] $ToolchainRoot,
    [switch] $TestOnly
)
. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\store-common.ps1"
$identity = Get-FrameTrailStoreIdentity $PackageName $Publisher $PublisherDisplayName -TestOnly:$TestOnly
Import-FrameTrailToolchain $ToolchainRoot
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory); $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new Store package directory; existing packages are retained.' }
$source = Get-Content -LiteralPath (Join-Path $PackageDirectory 'payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$release = ConvertTo-FrameTrailReleaseVersion (([version]$source.version).ToString(3))
if ($source.architecture -ne 'x64' -or $source.version -ne $release.FileVersion) { throw 'Store source must be an x64 X.Y.Z.0 product payload.' }
$storeVersion = Get-FrameTrailStoreVersion $release.Version
$original = Join-Path $PackageDirectory 'app'
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $original 'FrameTrail.App.dll')).Version.ToString() -ne $source.version) { throw 'Source application and payload versions differ.' }
$seen = @{}
foreach ($file in $source.files) {
    if ($file.path -match '(^/|:|\\|(^|/)\.\.(/|$))' -or $seen.ContainsKey($file.path)) { throw 'Unsafe or duplicate source payload path.' }
    $path = Join-Path $original $file.path
    if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FrameTrailSha256 $path) -ne $file.sha256) { throw "Source payload mismatch: $($file.path)" }
    $seen[$file.path] = $true
}
if (@(Get-ChildItem -LiteralPath $original -File -Recurse).Count -ne $seen.Count) { throw 'Unexpected source payload files.' }
$payload = Join-Path $OutputDirectory 'payload'; New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($file in $source.files) {
    $destination = Join-Path $payload $file.path; New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $original $file.path) -Destination $destination
}
$manifestPath = Join-Path $payload 'AppxManifest.xml'
Set-FrameTrailStoreManifest $manifestPath $identity $storeVersion
$tools = Get-FrameTrailPackagingTools
& $tools.MakePri new /pr $payload /cf (Join-Path $ProjectRoot 'packaging\priconfig.xml') /mn $manifestPath /of (Join-Path $payload 'resources.pri') /o > (Join-Path $OutputDirectory 'makepri.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Store PRI indexing failed; inspect makepri.log.' }
$suffix = if ($identity.TestOnly) { '-Store-TEST-ONLY' } else { '-Store' }
$name = "FrameTrail-$($release.Version)-x64$suffix.msix"
& $tools.MakeAppx pack /d $payload /p (Join-Path $OutputDirectory $name) /h SHA256 /o > (Join-Path $OutputDirectory 'makeappx.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Store MSIX schema validation failed; inspect makeappx.log.' }
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($payload.Length + 1).Replace('\','/'); bytes = $_.Length; sha256 = Get-FrameTrailSha256 $_.FullName } })
[ordered]@{ productVersion = $release.Version; packageVersion = $storeVersion; architecture = 'x64'; packageFile = $name; identity = $identity; testOnly = [bool]$identity.TestOnly; signature = 'unsigned-for-microsoft-store'; sourceRevision = $source.sourceRevision; sourceDirty = [bool]$source.sourceDirty; certification = 'not-run'; files = $files } |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'store-manifest.json') -Encoding UTF8
# ASCII source keeps the instructions readable when this script runs in Windows PowerShell 5.1.
$notice = if ($identity.TestOnly) { 'TEST ONLY: no registered Partner Center identity. DO NOT upload this package to the Store.' } else { 'Partner Center identity supplied. Upload the .msix file to the matching MSIX product in Partner Center.' }
$instructions = @"
# FrameTrail Microsoft Store package

$notice

- Product version: $($release.Version). Store package version: $storeVersion (product major + 1; minor/build unchanged; revision 0).
- Package: $name
- Identity/Name: $($identity.Name)
- Identity/Publisher: $($identity.Publisher)
- Properties/PublisherDisplayName: $($identity.PublisherDisplayName)
- Unsigned by design: Microsoft signs MSIX submissions after certification. No purchased signing certificate or PFX is required for this path.
- This unsigned upload package is not a public sideload installer. Use Setup.exe or the portable ZIP for GitHub downloads.
- Run Windows App Certification Kit and test an installed package before submission. Certification and Store acceptance have not run.
- Declare runFullTrust for native recording/editing and Android adb support; webcam/microphone are optional recording inputs. Complete the Store listing, privacy policy and certification notes.
- Source commit: $($source.sourceRevision). Full instructions: docs/microsoft-store.md in the repository.
"@
Write-FrameTrailUtf8 (Join-Path $OutputDirectory 'STORE-SUBMISSION.md') $instructions
$checksums = foreach ($asset in @($name,'store-manifest.json','STORE-SUBMISSION.md')) { (Get-FrameTrailSha256 (Join-Path $OutputDirectory $asset)) + '  ' + $asset }
$checksums | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
& "$PSScriptRoot\verify-store.ps1" -PackageDirectory $OutputDirectory
Write-Output "Store package: $(Join-Path $OutputDirectory $name). TestOnly=$($identity.TestOnly). Not signed, installed or submitted."
