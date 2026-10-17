[CmdletBinding()]
param()
. "$PSScriptRoot\store-common.ps1"
$checks = 0
function Assert-Store([bool] $Condition, [string] $Message) { if (!$Condition) { throw $Message }; $script:checks++ }
foreach ($example in @(@('0.0.1','1.0.1.0'),@('0.0.2','1.0.2.0'),@('0.9.0','1.9.0.0'),@('1.0.0','2.0.0.0'),@('65534.65534.65534','65535.65534.65534.0'))) { Assert-Store ((Get-FrameTrailStoreVersion $example[0]) -eq $example[1]) 'Invalid Store version mapping.' }
Assert-Store (([version](Get-FrameTrailStoreVersion '1.0.0')) -gt ([version](Get-FrameTrailStoreVersion '0.9.0'))) 'Store version went backwards at 1.0.'
$publisher = 'CN=12345678-1234-1234-1234-123456789012'
foreach ($invalid in @(@('','',''),@('Bad Name',$publisher,'Example'),@(('a' * 51),$publisher,'Example'),@('Example.App','CN=FrameTrail Development','Example'),@('Example.App','not-a-publisher','Example'),@('Example.App',$publisher,''),@('Example.App',$publisher,"Example`nInjected"),@('FrameTrail.StorePackagingTest',$publisher,'Example'))) {
    $rejected = $false; try { $null = Get-FrameTrailStoreIdentity $invalid[0] $invalid[1] $invalid[2] } catch { $rejected = $true }
    Assert-Store $rejected 'Accepted an invalid/placeholder Store identity.'
}
$identity = Get-FrameTrailStoreIdentity '12345.ExampleApp' $publisher 'Example & Co'
Assert-Store (!$identity.TestOnly -and $identity.Publisher -ceq $publisher) 'Configured identity was normalized.'
$fixture = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts\test-results\store-metadata-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$manifestPath = Join-Path $fixture 'AppxManifest.xml'
Copy-Item -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'packaging\AppxManifest.xml') -Destination $manifestPath
Set-FrameTrailStoreManifest $manifestPath $identity '1.0.1.0'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
Assert-Store ($manifest.Package.Identity.Name -ceq $identity.Name -and $manifest.Package.Identity.Publisher -ceq $publisher -and $manifest.Package.Identity.Version -eq '1.0.1.0') 'Store identity/version stamp mismatch.'
Assert-Store ($manifest.Package.Properties.PublisherDisplayName -ceq 'Example & Co') 'Store display-name XML escaping failed.'
Assert-Store ($manifest.Package.Applications.Application.Executable -eq 'FrameTrail.App.exe' -and $manifest.Package.Capabilities.Capability.Name -eq 'runFullTrust') 'Store stamp changed executable/capabilities.'
$testIdentity = Get-FrameTrailStoreIdentity -TestOnly
Assert-Store ($testIdentity.TestOnly -and $testIdentity.Name -eq 'FrameTrail.StorePackagingTest') 'Test mode is not clearly isolated.'
Write-Output "PASS: $checks Microsoft Store identity/version checks. Fixture: $fixture"
