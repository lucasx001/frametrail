[CmdletBinding()]
param()
. "$PSScriptRoot\release-common.ps1"
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('artifacts\test-results\release-metadata-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$checks = 0
function Assert-Release([bool] $Condition, [string] $Message) { if (!$Condition) { throw $Message }; $script:checks++ }
foreach ($invalid in @('','0.0.0','01.0.1','1.2','v1.2.3-beta','65536.0.1','65535.0.1','1.65535.1','1.1.65535','1.2.3;echo bad','1.2.3.4','9999999999999999.0.1')) {
    $rejected = $false; try { $null = ConvertTo-FrameTrailReleaseVersion $invalid } catch { $rejected = $true }
    Assert-Release $rejected "Accepted invalid version: $invalid"
}
foreach ($path in @('version.props','src\FrameTrail.App\app.manifest','packaging\AppxManifest.xml','src\FrameTrail.Workspace\packages.lock.json','tests\FrameTrail.App.Tests\packages.lock.json')) {
    $target = Join-Path $fixture $path; New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $path) -Destination $target
}
foreach ($value in @('v0.0.1','0.0.2','65534.65534.65534')) {
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
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $publisher = Join-Path $PSScriptRoot 'publish-release.ps1'
    $global:FrameTrailPublishFixture = @{ existing = $null; reference = $null; annotated = $null; apiFailure = $null; failUpload = $false; writes = [Collections.Generic.List[string]]::new() }
    # All GitHub calls in this test are an in-process fake. No network or release writes.
    function global:gh {
        param([Parameter(ValueFromRemainingArguments=$true)][string[]] $Arguments)
        $global:LASTEXITCODE = 0
        $state = $global:FrameTrailPublishFixture
        if ($Arguments[0] -eq 'api') {
            if ($state.apiFailure) { $global:LASTEXITCODE = 1; return "gh: API request failed (HTTP $($state.apiFailure))" }
            $value = switch -Regex ($Arguments[1]) {
                '/releases/tags/' { $state.existing; break }
                '/git/ref/tags/' { $state.reference; break }
                '/git/tags/' { $state.annotated; break }
                default { throw "Unexpected fake API route: $($Arguments[1])" }
            }
            if ($null -eq $value) { $global:LASTEXITCODE = 1; return 'gh: Not Found (HTTP 404)' }
            return ($value | ConvertTo-Json -Depth 8 -Compress)
        }
        if ($Arguments[0] -ne 'release') { throw 'Unexpected GitHub command.' }
        if ($Arguments[1] -eq 'create' -and '--draft' -notin $Arguments) { throw 'Release must be created as a draft.' }
        $state.writes.Add($Arguments[1])
        if ($Arguments[1] -eq 'upload' -and $state.failUpload) { $global:LASTEXITCODE = 1 }
    }
    $publishChecks = 0
    try {
        foreach ($scenario in @('first-version','lightweight','annotated','wrong-tag','public','matching-draft','wrong-draft','unauthorized','forbidden','server-error')) {
            $state = $global:FrameTrailPublishFixture
            $state.existing = $null; $state.reference = $null; $state.annotated = $null; $state.apiFailure = $null; $state.writes.Clear()
            switch ($scenario) {
                'lightweight' { $state.reference = @{ object = @{ type = 'commit'; sha = $source } } }
                'annotated' { $state.reference = @{ object = @{ type = 'tag'; sha = ('a' * 40) } }; $state.annotated = @{ object = @{ type = 'commit'; sha = $source } } }
                'wrong-tag' { $state.reference = @{ object = @{ type = 'commit'; sha = ('b' * 40) } } }
                'public' { $state.existing = @{ draft = $false; target_commitish = $source } }
                'matching-draft' { $state.existing = @{ draft = $true; target_commitish = $source } }
                'wrong-draft' { $state.existing = @{ draft = $true; target_commitish = ('c' * 40) } }
                'unauthorized' { $state.apiFailure = 401 }
                'forbidden' { $state.apiFailure = 403 }
                'server-error' { $state.apiFailure = 503 }
            }
            $failed = $false
            try { & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $source -CheckOnly | Out-Null } catch { $failed = $true }
            if ($failed -ne ($scenario -in @('wrong-tag','public','wrong-draft','unauthorized','forbidden','server-error')) -or $state.writes.Count) { throw "Publish preflight failed scenario: $scenario" }
            # GitHub's pwsh wrapper exits with LASTEXITCODE even when the script did
            # not throw. Expected 404 lookups must not leave a failure in the caller.
            if (!$failed -and $global:LASTEXITCODE -ne 0) { throw "Successful preflight left a nonzero process exit code: $scenario" }
            $publishChecks++
        }
        $state.apiFailure = $null
        foreach ($draft in @($false,$true)) {
            $state.existing = $null; $state.reference = $null; $state.writes.Clear()
            & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $source -AssetDirectory $assetDirectory -Draft:$draft | Out-Null
            $expected = if ($draft) { 'create,upload,view' } else { 'create,upload,edit,view' }
            if (($state.writes -join ',') -ne $expected) { throw 'Release was not created as draft, uploaded, then published in order.' }
            $publishChecks++
        }
        $state.existing = @{ draft = $true; target_commitish = $source }; $state.writes.Clear()
        & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $source -AssetDirectory $assetDirectory | Out-Null
        if (($state.writes -join ',') -ne 'upload,edit,view') { throw 'Same-commit draft retry failed.' }
        $publishChecks++
        $state.existing = $null; $state.writes.Clear(); $state.failUpload = $true
        $failed = $false
        try { & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $source -AssetDirectory $assetDirectory | Out-Null } catch { $failed = $true }
        if (!$failed -or ($state.writes -join ',') -ne 'create,upload') { throw 'Failed upload must never publish a release.' }
        $publishChecks++
    } finally {
        Remove-Item Function:\gh
        Remove-Variable FrameTrailPublishFixture -Scope Global
    }
    $global:LASTEXITCODE = 0
    Write-Output "PASS: $publishChecks isolated publish/preflight scenarios (no GitHub writes)."
}
