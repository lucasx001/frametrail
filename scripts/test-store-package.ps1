[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $PackageDirectory)
. "$PSScriptRoot\common.ps1"
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
& "$PSScriptRoot\verify-store.ps1" -PackageDirectory $PackageDirectory
$original = Get-Content -LiteralPath (Join-Path $PackageDirectory 'store-manifest.json') -Raw -Encoding UTF8
$manifest = $original | ConvertFrom-Json
$fixture = Join-Path $ProjectRoot ('artifacts\test-results\store-archive-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$names = @($manifest.packageFile,'store-manifest.json','STORE-SUBMISSION.md')
foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $PackageDirectory $name) -Destination $fixture }
$checks = 0
foreach ($failure in @('identity','version','inventory','signature')) {
    $record = $original | ConvertFrom-Json
    switch ($failure) {
        'identity' { $record.identity.Name = 'Another.StoreIdentity' }
        'version' { $record.packageVersion = '0.0.1.0' }
        'inventory' { $record.files = @($record.files | Select-Object -Skip 1) }
        'signature' { $record.signature = 'signed' }
    }
    $record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $fixture 'store-manifest.json') -Encoding UTF8
    $sums = foreach ($name in $names) { (Get-FrameTrailSha256 (Join-Path $fixture $name)) + '  ' + $name }
    $sums | Set-Content -LiteralPath (Join-Path $fixture 'SHA256SUMS.txt') -Encoding ASCII
    $rejected = $false; try { & "$PSScriptRoot\verify-store.ps1" -PackageDirectory $fixture | Out-Null } catch { $rejected = $true }
    if (!$rejected) { throw "Accepted a conflicting Store package: $failure" }
    $checks++
}
Write-Output "PASS: $checks Store archive tamper checks (including rehashed metadata). Fixture: $fixture"
