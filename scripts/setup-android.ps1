[CmdletBinding()]
param()
. "$PSScriptRoot\common.ps1"
$cache = Join-Path $ProjectRoot 'artifacts\android-downloads'
$runtimeDirectory = Join-Path $ProjectRoot 'artifacts\android-runtime'
New-Item -ItemType Directory -Force -Path $cache,$runtimeDirectory | Out-Null
function Get-Sha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path); $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Get-PinnedFile([string] $Url, [string] $Path, [string] $Sha256) {
    if (!(Test-Path -LiteralPath $Path)) { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path }
    if ((Get-Sha256 $Path) -ne $Sha256) { throw "Checksum mismatch: $Path. Remove this downloaded file and retry." }
}
$platformZip = Join-Path $cache 'platform-tools_r36.0.0-win.zip'
Get-PinnedFile 'https://dl.google.com/android/repository/platform-tools_r36.0.0-win.zip' $platformZip '12c2841f354e92a0eb2fd7bf6f0f9bf8538abce7bd6b060ac8349d6f6a61107c'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($platformZip)
try {
    foreach ($name in @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll','NOTICE.txt')) {
        $entry = $archive.GetEntry("platform-tools/$name"); if (!$entry) { throw "Missing runtime file: $name" }
        $targetName = if ($name -eq 'NOTICE.txt') { 'ADB-NOTICE.txt' } else { $name }
        $targetPath = Join-Path $runtimeDirectory $targetName
        $entryStream = $entry.Open(); $entryAlgorithm = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($entryAlgorithm.ComputeHash($entryStream)).Replace('-','') }
        finally { $entryStream.Dispose(); $entryAlgorithm.Dispose() }
        if ((Test-Path -LiteralPath $targetPath) -and (Get-Sha256 $targetPath) -eq $entryHash) { continue }
        $inputStream = $entry.Open(); $outputStream = [IO.File]::Create($targetPath)
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
} finally { $archive.Dispose() }
Get-PinnedFile 'https://github.com/Genymobile/scrcpy/releases/download/v3.3.4/scrcpy-server-v3.3.4' (Join-Path $runtimeDirectory 'scrcpy-server') '8588238c9a5a00aa542906b6ec7e6d5541d9ffb9b5d0f6e1bc0e365e2303079e'
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs\licenses\scrcpy-Apache-2.0.txt') -Destination (Join-Path $runtimeDirectory 'scrcpy-LICENSE.txt')
$hashes = [ordered]@{}
foreach ($name in @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll','scrcpy-server','ADB-NOTICE.txt','scrcpy-LICENSE.txt')) { $hashes[$name] = Get-Sha256 (Join-Path $runtimeDirectory $name) }
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'sha256.json') -Encoding UTF8
Write-Output "Pinned Android runtime ready: $runtimeDirectory. Build the app to include it."
