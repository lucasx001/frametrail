Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\release-common.ps1"

function Get-FrameTrailStoreIdentity([string] $PackageName, [string] $Publisher, [string] $PublisherDisplayName, [switch] $TestOnly) {
    if ($TestOnly) {
        return [pscustomobject]@{ Name = 'FrameTrail.StorePackagingTest'; Publisher = 'CN=00000000-0000-0000-0000-000000000001'; PublisherDisplayName = 'FrameTrail Packaging Test'; TestOnly = $true }
    }
    if ($PackageName -cnotmatch '^[A-Za-z0-9.-]{3,50}$') { throw 'Set STORE_PACKAGE_NAME to Package/Identity/Name from Partner Center (3..50 ASCII letters, numbers, dots or hyphens).' }
    if ([string]::IsNullOrWhiteSpace($Publisher) -or !$Publisher.StartsWith('CN=', [StringComparison]::Ordinal) -or $Publisher -eq 'CN=FrameTrail Development' -or $Publisher -match '[\r\n]') { throw 'Set STORE_PUBLISHER to the exact Package/Identity/Publisher from Partner Center.' }
    $null = New-Object Security.Cryptography.X509Certificates.X500DistinguishedName($Publisher)
    if ([string]::IsNullOrWhiteSpace($PublisherDisplayName) -or $PublisherDisplayName.Length -gt 256 -or $PublisherDisplayName -match '[\r\n]') { throw 'Set STORE_PUBLISHER_DISPLAY_NAME to Package/Properties/PublisherDisplayName from Partner Center.' }
    if ($PackageName -eq 'FrameTrail.StorePackagingTest' -or $Publisher -eq 'CN=00000000-0000-0000-0000-000000000001') { throw 'Test identities cannot be used for Store submission.' }
    return [pscustomobject]@{ Name = $PackageName; Publisher = $Publisher; PublisherDisplayName = $PublisherDisplayName; TestOnly = $false }
}

function Get-FrameTrailStoreVersion([string] $ProductVersion) {
    $release = ConvertTo-FrameTrailReleaseVersion $ProductVersion
    $version = [version]$release.Version
    # Store reserves revision=0 and requires a nonzero major version. Offset every
    # major (not only 0.x) so 0.9 -> 1.0 never lowers the Store package version.
    return "$($version.Major + 1).$($version.Minor).$($version.Build).0"
}

function Set-FrameTrailStoreManifest([string] $Path, $Identity, [string] $Version) {
    [xml]$manifest = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    $manifest.Package.Identity.Name = $Identity.Name
    $manifest.Package.Identity.Publisher = $Identity.Publisher
    $manifest.Package.Identity.Version = $Version
    $manifest.Package.Properties.PublisherDisplayName = $Identity.PublisherDisplayName
    $manifest.Save($Path)
}
