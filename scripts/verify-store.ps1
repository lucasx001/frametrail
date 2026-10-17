[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $PackageDirectory)
. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\store-common.ps1"
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$record = Get-Content -LiteralPath (Join-Path $PackageDirectory 'store-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$identity = Get-FrameTrailStoreIdentity $record.identity.Name $record.identity.Publisher $record.identity.PublisherDisplayName -TestOnly:$record.testOnly
if ($record.identity.TestOnly -ne $record.testOnly -or $record.identity.Name -cne $identity.Name -or $record.identity.Publisher -cne $identity.Publisher -or $record.identity.PublisherDisplayName -cne $identity.PublisherDisplayName -or $record.architecture -ne 'x64') { throw 'Store identity mode and metadata conflict.' }
$release = ConvertTo-FrameTrailReleaseVersion $record.productVersion
$suffix = if ($record.testOnly) { '-Store-TEST-ONLY' } else { '-Store' }
if ($record.packageFile -cne "FrameTrail-$($release.Version)-x64$suffix.msix" -or $record.packageVersion -ne (Get-FrameTrailStoreVersion $release.Version) -or $record.signature -ne 'unsigned-for-microsoft-store') { throw 'Store package metadata/version mismatch.' }
$assets = @($record.packageFile,'store-manifest.json','STORE-SUBMISSION.md'); $seen = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $PackageDirectory 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Invalid Store checksum entry.' }
    $hash = $Matches[1]; $name = $Matches[2]
    if ($name -notin $assets -or $seen.ContainsKey($name) -or (Get-FrameTrailSha256 (Join-Path $PackageDirectory $name)) -ne $hash) { throw 'Store asset checksum mismatch.' }
    $seen[$name] = $true
}
if ($seen.Count -ne $assets.Count) { throw 'Incomplete Store checksum list.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $PackageDirectory $record.packageFile))
function Read-StoreXml([string] $Name) {
    $entry = $archive.GetEntry($Name)
    if (!$entry) { throw "Missing Store package XML: $Name" }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { return [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
}
try {
    if ($archive.GetEntry('AppxSignature.p7x')) { throw 'Store upload package must remain unsigned by this build.' }
    $manifest = Read-StoreXml 'AppxManifest.xml'
    if ($manifest.Package.Identity.Name -cne $identity.Name -or $manifest.Package.Identity.Publisher -cne $identity.Publisher -or $manifest.Package.Properties.PublisherDisplayName -cne $identity.PublisherDisplayName -or $manifest.Package.Identity.Version -ne $record.packageVersion -or $manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Archived Store identity does not match submission metadata.' }
    $blockMap = Read-StoreXml 'AppxBlockMap.xml'
    if ($blockMap.BlockMap.HashMethod -ne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Store block map must use SHA-256.' }
    $inventory = @{}
    foreach ($file in $record.files) {
        if ($file.path -match '(^/|:|\\|(^|/)\.\.(/|$))' -or $inventory.ContainsKey($file.path)) { throw 'Unsafe or duplicate Store inventory path.' }
        $entry = $archive.GetEntry($file.path); if (!$entry) { $entry = $archive.GetEntry($file.path.Replace('/','\')) }
        if (!$entry -or $entry.Length -ne $file.bytes) { throw "Missing Store payload: $($file.path)" }
        $hash = [Security.Cryptography.SHA256]::Create(); $stream = $entry.Open()
        try { $actual = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $stream.Dispose(); $hash.Dispose() }
        if ($actual -ne $file.sha256) { throw "Store payload checksum mismatch: $($file.path)" }
        $inventory[$file.path] = $true
    }
    foreach ($entry in $archive.Entries) {
        $path = $entry.FullName.Replace('\','/')
        if (!$path.EndsWith('/') -and !$inventory.ContainsKey($path) -and $path -notin @('[Content_Types].xml','AppxBlockMap.xml','AppxMetadata/CodeIntegrity.cat')) { throw "Unlisted Store package file: $path" }
    }
    foreach ($required in @('AppxManifest.xml','resources.pri','FrameTrail.App.pri','App.xbf','MainWindow.xbf','FrameTrail.App.exe','coreclr.dll','FrameTrail.Engine.dll','FrameTrail.Media.dll')) { if (!$inventory.ContainsKey($required)) { throw "Store payload missing $required" } }
} finally { $archive.Dispose() }
Write-Output "Verified Store identity/version, unsigned status, SHA-256 block map and $($record.files.Count) payload files. TestOnly=$($record.testOnly)."
