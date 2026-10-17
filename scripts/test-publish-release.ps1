#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $AssetDirectory, [Parameter(Mandatory=$true)][string] $ExpectedCommit)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publisher = Join-Path $PSScriptRoot 'publish-release.ps1'
$global:FrameTrailPublishFixture = @{ existing = $null; reference = $null; annotated = $null; failUpload = $false; writes = [Collections.Generic.List[string]]::new() }
# All GitHub calls in this test are an in-process fake. No network or release writes.
function global:gh {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]] $Arguments)
    $global:LASTEXITCODE = 0
    $state = $global:FrameTrailPublishFixture
    if ($Arguments[0] -eq 'api') {
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
$checks = 0
try {
    foreach ($scenario in @('first-version','lightweight','annotated','wrong-tag','public','matching-draft','wrong-draft')) {
        $state = $global:FrameTrailPublishFixture
        $state.existing = $null; $state.reference = $null; $state.annotated = $null; $state.writes.Clear()
        switch ($scenario) {
            'lightweight' { $state.reference = @{ object = @{ type = 'commit'; sha = $ExpectedCommit } } }
            'annotated' { $state.reference = @{ object = @{ type = 'tag'; sha = ('a' * 40) } }; $state.annotated = @{ object = @{ type = 'commit'; sha = $ExpectedCommit } } }
            'wrong-tag' { $state.reference = @{ object = @{ type = 'commit'; sha = ('b' * 40) } } }
            'public' { $state.existing = @{ draft = $false; target_commitish = $ExpectedCommit } }
            'matching-draft' { $state.existing = @{ draft = $true; target_commitish = $ExpectedCommit } }
            'wrong-draft' { $state.existing = @{ draft = $true; target_commitish = ('c' * 40) } }
        }
        $failed = $false
        try { & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $ExpectedCommit -CheckOnly | Out-Null } catch { $failed = $true }
        if ($failed -ne ($scenario -in @('wrong-tag','public','wrong-draft')) -or $state.writes.Count) { throw "Publish preflight failed scenario: $scenario" }
        $checks++
    }
    foreach ($draft in @($false,$true)) {
        $state.existing = $null; $state.reference = $null; $state.writes.Clear()
        & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $ExpectedCommit -AssetDirectory $AssetDirectory -Draft:$draft | Out-Null
        $expected = if ($draft) { 'create,upload,view' } else { 'create,upload,edit,view' }
        if (($state.writes -join ',') -ne $expected) { throw 'Release was not created as draft, uploaded, then published in order.' }
        $checks++
    }
    $state.existing = @{ draft = $true; target_commitish = $ExpectedCommit }; $state.writes.Clear()
    & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $ExpectedCommit -AssetDirectory $AssetDirectory | Out-Null
    if (($state.writes -join ',') -ne 'upload,edit,view') { throw 'Same-commit draft retry failed.' }
    $checks++
    $state.existing = $null; $state.writes.Clear(); $state.failUpload = $true
    $failed = $false
    try { & $publisher -Repository fixture/test -Version 0.0.1 -ExpectedCommit $ExpectedCommit -AssetDirectory $AssetDirectory | Out-Null } catch { $failed = $true }
    if (!$failed -or ($state.writes -join ',') -ne 'create,upload') { throw 'Failed upload must never publish a release.' }
    $checks++
} finally {
    Remove-Item Function:\gh
    Remove-Variable FrameTrailPublishFixture -Scope Global
}
$global:LASTEXITCODE = 0
Write-Output "PASS: $checks isolated publish/preflight scenarios (no GitHub writes)."
