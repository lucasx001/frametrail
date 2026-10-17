[CmdletBinding()]
param()
. "$PSScriptRoot\release-common.ps1"
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('artifacts\test-results\release-metadata-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$checks = 0
function Assert-Release([bool] $Condition, [string] $Message) { if (!$Condition) { throw $Message }; $script:checks++ }
foreach ($invalid in @('','0.0.0','01.0.1','1.2','v1.2.3-beta','65536.0.1','1.2.3;echo bad','1.2.3.4','9999999999999999.0.1')) {
    $rejected = $false; try { $null = ConvertTo-FrameTrailReleaseVersion $invalid } catch { $rejected = $true }
    Assert-Release $rejected "Accepted invalid version: $invalid"
}
foreach ($path in @('version.props','src\FrameTrail.App\app.manifest','packaging\AppxManifest.xml','src\FrameTrail.Workspace\packages.lock.json','tests\FrameTrail.App.Tests\packages.lock.json')) {
    $target = Join-Path $fixture $path; New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $path) -Destination $target
}
foreach ($value in @('v0.0.1','0.0.2','65535.65535.65535')) {
    $release = Set-FrameTrailReleaseVersion $fixture $value
    [xml] $props = Get-Content (Join-Path $fixture 'version.props') -Raw -Encoding UTF8
    [xml] $manifest = Get-Content (Join-Path $fixture 'packaging\AppxManifest.xml') -Raw -Encoding UTF8
    [xml] $app = Get-Content (Join-Path $fixture 'src\FrameTrail.App\app.manifest') -Raw -Encoding UTF8
    Assert-Release ($props.Project.PropertyGroup.Version -eq $release.Version) 'Semantic version mismatch'
    Assert-Release ($props.Project.PropertyGroup.FileVersion -eq $release.FileVersion -and $manifest.Package.Identity.Version -eq $release.FileVersion -and $app.assembly.assemblyIdentity.version -eq $release.FileVersion) 'Executable/MSIX version mismatch'
    $lock = Get-Content (Join-Path $fixture 'tests\FrameTrail.App.Tests\packages.lock.json') -Raw | ConvertFrom-Json
    Assert-Release ($lock.dependencies.'net8.0'.'frametrail.workspace'.dependencies.'FrameTrail.Interop' -eq "[$($release.Version), )") 'Local dependency lock not updated'
    Assert-Release ($lock.dependencies.'net8.0'.xunit.resolved -eq '2.9.3') 'External dependency changed'
}
$assetDirectory = Join-Path $fixture 'public-assets'; New-Item -ItemType Directory -Path $assetDirectory | Out-Null
$source = '1234567890123456789012345678901234567890'
$names = @('FrameTrail-0.0.1-x64-Setup.exe','FrameTrail-0.0.1-x64-portable.zip','payload-manifest.json','nuget-dependencies.lock.json','INSTALL.zh-CN.md')
foreach ($name in $names) { Write-FrameTrailUtf8 (Join-Path $assetDirectory $name) "fixture-$name" }
$inventory = @($names | ForEach-Object { $file = Get-Item -LiteralPath (Join-Path $assetDirectory $_); @{ name = $_; bytes = $file.Length; sha256 = Get-FrameTrailReleaseHash $file.FullName } })
Write-FrameTrailUtf8 (Join-Path $assetDirectory 'release-manifest.json') (@{ version = '0.0.1'; tag = 'v0.0.1'; sourceRevision = $source; msixIncluded = $false; files = $inventory } | ConvertTo-Json -Depth 6)
$sumsPath = Join-Path $assetDirectory 'SHA256SUMS.txt'
$sums = (@(Get-ChildItem -LiteralPath $assetDirectory -File | Sort-Object Name | ForEach-Object { (Get-FrameTrailReleaseHash $_.FullName) + '  ' + $_.Name }) -join "`n") + "`n"
Write-FrameTrailUtf8 $sumsPath $sums
Test-FrameTrailReleaseAssets $assetDirectory '0.0.1' $source; $checks++
foreach ($failure in @('version','commit','missing','tamper','extra','duplicate','traversal')) {
    $arguments = @($assetDirectory,'0.0.1',$source)
    $target = Join-Path $assetDirectory $names[0]
    switch ($failure) {
        'version' { $arguments[1] = '0.0.2' }
        'commit' { $arguments[2] = '2234567890123456789012345678901234567890' }
        'missing' { Move-Item -LiteralPath $target -Destination ($target + '.bak') }
        'tamper' { Write-FrameTrailUtf8 $target 'tampered' }
        'extra' { Write-FrameTrailUtf8 (Join-Path $assetDirectory 'extra.txt') 'extra' }
        'duplicate' { Write-FrameTrailUtf8 $sumsPath ($sums + ($sums -split "`n")[0] + "`n") }
        'traversal' { Write-FrameTrailUtf8 $sumsPath ($sums + ('0' * 64) + '  ../outside.txt') }
    }
    $rejected = $false; try { Test-FrameTrailReleaseAssets @arguments } catch { $rejected = $true }
    Assert-Release $rejected "Accepted invalid release assets: $failure"
    switch ($failure) {
        'missing' { Move-Item -LiteralPath ($target + '.bak') -Destination $target }
        'tamper' { Write-FrameTrailUtf8 $target "fixture-$($names[0])" }
        'extra' { Remove-Item -LiteralPath (Join-Path $assetDirectory 'extra.txt') }
        { $_ -in @('duplicate','traversal') } { Write-FrameTrailUtf8 $sumsPath $sums }
    }
}
Write-Output "PASS: $checks release metadata and asset checks. Fixture: $fixture"
if ($PSVersionTable.PSVersion.Major -ge 7) { & "$PSScriptRoot\test-publish-release.ps1" -AssetDirectory $assetDirectory -ExpectedCommit $source }
