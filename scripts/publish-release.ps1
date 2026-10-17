#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $Repository,
    [Parameter(Mandatory=$true)][string] $Version,
    [Parameter(Mandatory=$true)][string] $ExpectedCommit,
    [string] $AssetDirectory,
    [switch] $Draft,
    [switch] $CheckOnly
)
. "$PSScriptRoot/release-common.ps1"
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $ExpectedCommit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid repository or source commit.' }
$release = ConvertTo-FrameTrailReleaseVersion $Version
function Read-GitHubObject([string] $Endpoint) {
    $output = & gh api $Endpoint 2>&1
    if ($LASTEXITCODE -eq 0) { return ($output | Out-String | ConvertFrom-Json) }
    if (($output | Out-String) -match 'HTTP 404') { return $null }
    throw "GitHub API request failed: $Endpoint"
}
function Invoke-Gh([string[]] $Arguments) { & gh @Arguments; if ($LASTEXITCODE -ne 0) { throw 'GitHub release operation failed.' } }
$existing = Read-GitHubObject "repos/$Repository/releases/tags/$($release.Tag)"
if ($existing -and !$existing.draft) { throw 'This version is already public. Publish a new version; existing public assets are never replaced.' }
$tagReference = Read-GitHubObject "repos/$Repository/git/ref/tags/$($release.Tag)"
$tagCommit = if ($tagReference) { $tagReference.object } else { $null }
$depth = 0
while ($tagCommit -and $tagCommit.type -eq 'tag') {
    if (++$depth -gt 8) { throw 'Release tag nesting is too deep.' }
    $annotated = Read-GitHubObject "repos/$Repository/git/tags/$($tagCommit.sha)"
    if (!$annotated) { throw 'Annotated release tag is missing.' }
    $tagCommit = $annotated.object
}
if ($tagCommit -and ($tagCommit.type -ne 'commit' -or $tagCommit.sha -ne $ExpectedCommit)) { throw 'Release tag points to a different source commit.' }
if ($existing -and !$tagCommit -and $existing.target_commitish -ne $ExpectedCommit) { throw 'Existing draft was created from another source commit.' }
if ($CheckOnly) { Write-Output "Release preflight passed: $($release.Tag) @ $ExpectedCommit"; return }
if (!$AssetDirectory) { throw 'AssetDirectory is required for publication.' }
$AssetDirectory = [IO.Path]::GetFullPath($AssetDirectory)
Test-FrameTrailReleaseAssets $AssetDirectory $release.Version $ExpectedCommit
if (!$existing) { Invoke-Gh @('release','create',$release.Tag,'--repo',$Repository,'--target',$ExpectedCommit,'--title',"FrameTrail $($release.Version)",'--notes-file',(Join-Path $AssetDirectory 'INSTALL.zh-CN.md'),'--draft') }
$assets = @(Get-ChildItem -LiteralPath $AssetDirectory -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
# Re-runs may repair only a draft. Public releases were rejected before any write.
Invoke-Gh (@('release','upload',$release.Tag,'--repo',$Repository,'--clobber') + $assets)
if (!$Draft) { Invoke-Gh @('release','edit',$release.Tag,'--repo',$Repository,'--draft=false') }
Invoke-Gh @('release','view',$release.Tag,'--repo',$Repository,'--json','url,isDraft,tagName,assets')
