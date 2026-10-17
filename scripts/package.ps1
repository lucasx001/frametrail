[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $NativeBuildDirectory,
    [string] $ToolchainRoot,
    [string] $Publisher = 'CN=FrameTrail Development',
    [string] $SigningCertificateThumbprint,
    [uri] $UpdateBaseUri,
    [switch] $SkipBuild,
    [switch] $BuildInstaller,
    [string] $InstallerCompiler
)
. "$PSScriptRoot\common.ps1"
Import-FrameTrailToolchain $ToolchainRoot
if (!$NativeBuildDirectory) { $NativeBuildDirectory = Join-Path $ProjectRoot 'artifacts\native' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $ProjectRoot ('artifacts\packages\candidate-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new package directory; existing candidates are retained.' }
if ($UpdateBaseUri -and (!$UpdateBaseUri.IsAbsoluteUri -or $UpdateBaseUri.Scheme -ne 'https' -or $UpdateBaseUri.UserInfo -or $UpdateBaseUri.Query -or $UpdateBaseUri.Fragment -or !$UpdateBaseUri.AbsoluteUri.EndsWith('/'))) { throw 'UpdateBaseUri must be an HTTPS directory URI ending in / without credentials, query or fragment.' }
[xml] $versions = Get-Content (Join-Path $ProjectRoot 'version.props') -Raw -Encoding UTF8
$releaseVersion = [string]$versions.Project.PropertyGroup.Version
$version = [string]$versions.Project.PropertyGroup.FileVersion
[xml] $manifest = Get-Content (Join-Path $ProjectRoot 'packaging\AppxManifest.xml') -Raw -Encoding UTF8
[xml] $executableManifest = Get-Content (Join-Path $ProjectRoot 'src\FrameTrail.App\app.manifest') -Raw -Encoding UTF8
if ($manifest.Package.Identity.Version -ne $version -or $executableManifest.assembly.assemblyIdentity.version -ne $version) { throw 'Version mismatch: version.props, app.manifest and AppxManifest.xml must agree.' }
if (!$SkipBuild) { & "$PSScriptRoot\build.ps1" -Configuration Release -NativeBuildDirectory $NativeBuildDirectory -ToolchainRoot $ToolchainRoot -LockedRestore }
$packagingTools = Get-FrameTrailPackagingTools
$makeappx = $packagingTools.MakeAppx; $makepri = $packagingTools.MakePri
$dotnet = Resolve-FrameTrailTool 'dotnet'
$payload = Join-Path $OutputDirectory 'app'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
# Publish .NET and Windows App SDK locally; do not depend on a developer runtime or PATH.
Invoke-Checked $dotnet @('publish',(Join-Path $ProjectRoot 'src\FrameTrail.App\FrameTrail.App.csproj'),'-c','Release','-p:Platform=x64','--self-contained','true','-p:RestoreLockedMode=true',"-p:NativeBuildDirectory=$NativeBuildDirectory",'-o',$payload)
foreach ($module in @('Engine','Capture','Audio','Recording','Media')) {
    $source = Join-Path (Get-NativeBinaryDirectory $NativeBuildDirectory 'Release') "FrameTrail.$module.dll"
    if (!(Test-Path -LiteralPath $source)) { throw "Missing native module: $module" }
    Copy-Item -LiteralPath $source -Destination $payload
}
$licenses = Join-Path $payload 'Licenses'; New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$runtimeConfig = Get-Content -LiteralPath (Join-Path $payload 'FrameTrail.App.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$runtimeVersion = $runtimeConfig.runtimeOptions.includedFrameworks[0].version
$nuget = Join-Path $env:USERPROFILE '.nuget\packages'; if ($env:NUGET_PACKAGES) { $nuget = $env:NUGET_PACKAGES }
Copy-Item -LiteralPath (Join-Path $nuget "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT") -Destination (Join-Path $licenses 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $nuget "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $licenses 'dotnet-NOTICES.txt')
[xml] $appProject = Get-Content (Join-Path $ProjectRoot 'src\FrameTrail.App\FrameTrail.App.csproj') -Raw -Encoding UTF8
$appSdkVersion = ($appProject.Project.ItemGroup.PackageReference | Where-Object Include -eq 'Microsoft.WindowsAppSDK').Version
Copy-Item -LiteralPath (Join-Path $nuget "microsoft.windowsappsdk\$appSdkVersion\NOTICE.txt") -Destination (Join-Path $licenses 'WindowsAppSDK-NOTICE.txt')
Copy-Item -LiteralPath (Join-Path $NativeBuildDirectory '_deps\nlohmann_json-src\LICENSE.MIT') -Destination (Join-Path $licenses 'nlohmann-json-MIT.txt')
foreach ($required in @('FrameTrail.App.exe','FrameTrail.App.dll','FrameTrail.App.pri','App.xbf','MainWindow.xbf','EditorWindow.xbf','RecorderStyles.xbf','EditorStyles.xbf','coreclr.dll','hostfxr.dll','Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll','Assets\Fonts\LICENSE.txt','Assets\Brand\FrameTrail.Icon.v1.ico','PackageAssets\StoreLogo.png','PackageAssets\Square44x44Logo.png','PackageAssets\Square150x150Logo.png','Licenses\dogsc-MIT.txt','THIRD-PARTY-NOTICES.md','Docs\privacy.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Candidate is missing $required" }
}
Get-ChildItem -LiteralPath $payload -Filter '*.pdb' -Recurse | Remove-Item
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Save((Join-Path $payload 'AppxManifest.xml'))
# Reindex the build's PRI under the package identity so shell icon qualifiers
# and embedded WinUI XAML resources are available together in resources.pri.
& $makepri new /pr $payload /cf (Join-Path $ProjectRoot 'packaging\priconfig.xml') /mn (Join-Path $payload 'AppxManifest.xml') /of (Join-Path $payload 'resources.pri') /o > (Join-Path $OutputDirectory 'makepri.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Package icon indexing failed; see makepri.log in the output directory.' }
$name = "FrameTrail-$releaseVersion-x64"
$package = Join-Path $OutputDirectory ($name + '.msix')
& $makeappx pack /d $payload /p $package /o > (Join-Path $OutputDirectory 'makeappx.log') 2>&1
if ($LASTEXITCODE -ne 0) { throw 'MSIX validation failed; see makeappx.log in the output directory.' }
$signature = 'unsigned-development-candidate'
if ($SigningCertificateThumbprint) {
    if ($SigningCertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Invalid signing certificate thumbprint.' }
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$SigningCertificateThumbprint"
    if (!$certificate.HasPrivateKey -or $certificate.Subject -ne $Publisher -or $certificate.NotAfter -le (Get-Date)) { throw 'Signing certificate must have a private key, be unexpired, and exactly match Publisher.' }
    $signtool = Join-Path (Split-Path $makeappx) 'signtool.exe'
    if (!(Test-Path -LiteralPath $signtool)) { $signtool = Resolve-FrameTrailTool 'signtool.exe' }
    Invoke-Checked $signtool @('sign','/fd','SHA256','/sha1',$SigningCertificateThumbprint,$package)
    Invoke-Checked $signtool @('verify','/pa','/v',$package)
    $signature = 'signed'; Export-Certificate -Cert $certificate -FilePath (Join-Path $OutputDirectory 'publisher.cer') | Out-Null
}
if ($UpdateBaseUri) {
    $feed = New-Object Xml.XmlDocument
    $feed.LoadXml('<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"><MainPackage /></AppInstaller>')
    $mainPackage = $feed.DocumentElement.FirstChild
    $feed.DocumentElement.SetAttribute('Uri',([uri]::new($UpdateBaseUri, 'FrameTrail.appinstaller').AbsoluteUri)); $feed.DocumentElement.SetAttribute('Version',$version)
    $mainPackage.SetAttribute('Name','FrameTrail.Desktop'); $mainPackage.SetAttribute('Publisher',$Publisher)
    $mainPackage.SetAttribute('Version',$version); $mainPackage.SetAttribute('ProcessorArchitecture','x64')
    $mainPackage.SetAttribute('Uri',([uri]::new($UpdateBaseUri, ($name + '.msix')).AbsoluteUri))
    # Manual update only: no OnLaunch or background installation that could interrupt work.
    $feed.Save((Join-Path $OutputDirectory 'FrameTrail.appinstaller'))
}
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($payload.TrimEnd('\').Length + 1).Replace('\','/'); bytes = $_.Length; sha256 = (Get-FrameTrailSha256 $_.FullName) }
})
$revision = (& git -C $ProjectRoot rev-parse HEAD).Trim(); $dirty = [bool](& git -C $ProjectRoot status --porcelain)
[ordered]@{ version = $version; releaseVersion = $releaseVersion; architecture = 'x64'; minimumWindowsBuild = 22621; publisher = $Publisher; signature = $signature; installerSignature = $(if ($BuildInstaller) { 'unsigned' } else { 'not-built' }); sourceRevision = $revision; sourceDirty = $dirty; files = $files } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'payload-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'src\FrameTrail.App\packages.lock.json') -Destination (Join-Path $OutputDirectory 'nuget-dependencies.lock.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($payload,(Join-Path $OutputDirectory ($name + '-portable.zip')))
if ($BuildInstaller) { & "$PSScriptRoot\package-installer.ps1" -PackageDirectory $OutputDirectory -InstallerCompiler $InstallerCompiler }
Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | ForEach-Object { (Get-FrameTrailSha256 $_.FullName) + '  ' + $_.Name } | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "Candidate: $OutputDirectory ($signature). No package was installed or published."
