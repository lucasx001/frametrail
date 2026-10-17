[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $PackageDirectory, [Parameter(Mandatory=$true)][string] $InstallDirectory, [switch] $SmokeTest)
. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\release-common.ps1"
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$manifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$release = ConvertTo-FrameTrailReleaseVersion (([version]$manifest.version).ToString(3))
$installer = Join-Path $PackageDirectory "FrameTrail-$($release.Version)-x64-Setup.exe"
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FrameTrail.Desktop_is1'
if (Test-Path -LiteralPath $InstallDirectory) { throw 'Choose a fresh, dedicated installer test directory.' }
if (Test-Path -LiteralPath $registry) { throw 'FrameTrail is already installed for this user; installer validation will not replace it.' }
$parent = Split-Path $InstallDirectory -Parent
if (!(Test-Path -LiteralPath $parent -PathType Container) -or !$InstallDirectory.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation test parent.' }
$resultDirectory = Join-Path $PackageDirectory 'installer-validation'; New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$sentinel = Join-Path $parent ('frametrail-user-project-' + [guid]::NewGuid().ToString('N') + '.txt')
Set-Content -LiteralPath $sentinel -Value 'User project must survive uninstall.'
function Run-Setup([string] $Executable, [string[]] $Arguments, [switch] $ExpectBlocked) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(90000)) { throw "Installer process $($process.Id) timed out." }
    if ($ExpectBlocked) {
        if ($process.ExitCode -eq 0) { throw 'Installer ignored the running application guard.' }
    } elseif ($process.ExitCode -ne 0) { throw "Installer exited with $($process.ExitCode); inspect logs in $resultDirectory." }
}
$installed = $false
$setupArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS','/TASKS=!desktopicon',('/DIR="' + $InstallDirectory + '"'))
# Simulate another application handle without opening or ending a media task.
$guard = New-Object Threading.Mutex($false, 'Local\FrameTrail.App.Running')
try {
    Run-Setup $installer ($setupArguments + @('/LOG="' + (Join-Path $resultDirectory 'blocked-install.log') + '"')) -ExpectBlocked
    if ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath (Join-Path $InstallDirectory 'FrameTrail.App.exe'))) { throw 'Blocked install changed the application.' }
} finally { $guard.Dispose() }
try {
    Run-Setup $installer ($setupArguments + @('/LOG="' + (Join-Path $resultDirectory 'install.log') + '"'))
    $installed = $true
    foreach ($file in $manifest.files) { if ((Get-FrameTrailSha256 (Join-Path $InstallDirectory $file.path)) -ne $file.sha256) { throw "Installed payload mismatch: $($file.path)" } }
    if (!(Test-Path -LiteralPath $registry)) { throw 'Missing current-user uninstall registration.' }
    Run-Setup $installer ($setupArguments + @('/LOG="' + (Join-Path $resultDirectory 'reinstall.log') + '"'))
    foreach ($file in $manifest.files) { if ((Get-FrameTrailSha256 (Join-Path $InstallDirectory $file.path)) -ne $file.sha256) { throw "Reinstalled payload mismatch: $($file.path)" } }
    if ($SmokeTest) {
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = Join-Path $InstallDirectory 'FrameTrail.App.exe'; $start.WorkingDirectory = $InstallDirectory
        $start.Arguments = '--smoke-test "' + (Join-Path $resultDirectory 'smoke.json') + '"'; $start.UseShellExecute = $false
        $start.EnvironmentVariables['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
        foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_STARTUP_HOOKS')) { $start.EnvironmentVariables.Remove($name) }
        $process = [Diagnostics.Process]::Start($start)
        if (!$process.WaitForExit(60000) -or $process.ExitCode -ne 0) { throw 'Installed application smoke failed.' }
        $smoke = Get-Content (Join-Path $resultDirectory 'smoke.json') -Raw | ConvertFrom-Json
        if (!$smoke.success -or $smoke.version -ne $release.Version -or !$smoke.runtimeDirectory.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Installed application loaded an unexpected runtime/version.' }
    }
    $guard = New-Object Threading.Mutex($false, 'Local\FrameTrail.App.Running')
    try {
        Run-Setup (Join-Path $InstallDirectory 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $resultDirectory 'blocked-uninstall.log') + '"')) -ExpectBlocked
        if (!(Test-Path -LiteralPath (Join-Path $InstallDirectory 'FrameTrail.App.exe')) -or !(Test-Path -LiteralPath $registry)) { throw 'Blocked uninstall removed the application.' }
    } finally { $guard.Dispose() }
} finally {
    if ($installed) { Run-Setup (Join-Path $InstallDirectory 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $resultDirectory 'uninstall.log') + '"')) }
}
if ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath (Join-Path $InstallDirectory 'FrameTrail.App.exe'))) { throw 'Uninstaller did not remove the application/registration.' }
if (!(Test-Path -LiteralPath $sentinel)) { throw 'Uninstaller removed an external user file.' }
[ordered]@{ success = $true; version = $release.Version; payloadFiles = $manifest.files.Count; install = $true; sameVersionReinstall = $true; runningAppInstallBlocked = $true; runningAppUninstallBlocked = $true; uninstall = $true; externalFilePreserved = $true; smokeTest = [bool]$SmokeTest } | ConvertTo-Json | Set-Content (Join-Path $resultDirectory 'result.json') -Encoding UTF8
Write-Output 'Installer installation, payload and uninstall verification passed.'
