[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $PackageDirectory, [switch] $SmokeTest, [switch] $WorkflowTest)
. "$PSScriptRoot\common.ps1"
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$payload = Join-Path $PackageDirectory 'app'; $expected = @{}
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'FrameTrail.App.dll')).Version
if ($assemblyVersion.ToString() -ne $manifest.version) { throw 'Application assembly and package versions differ.' }
foreach ($entry in $manifest.files) {
    if ($entry.path -match '(^/|:|\\|(^|/)\.\.(/|$))' -or $expected.ContainsKey($entry.path)) { throw 'Unsafe or duplicate payload path.' }
    $file = Join-Path $payload $entry.path
    if ((Get-Item -LiteralPath $file).Length -ne $entry.bytes -or (Get-FrameTrailSha256 $file) -ne $entry.sha256) { throw "Payload checksum mismatch: $($entry.path)" }
    $expected[$entry.path] = $entry
}
if (@(Get-ChildItem -LiteralPath $payload -File -Recurse).Count -ne $expected.Count) { throw 'Unexpected files in candidate payload.' }
foreach ($line in Get-Content -LiteralPath (Join-Path $PackageDirectory 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-f0-9]{64})  ([^\\/:]+)$') { throw 'Invalid checksum manifest.' }
    if ((Get-FrameTrailSha256 (Join-Path $PackageDirectory $Matches[2])) -ne $Matches[1]) { throw 'Candidate checksum mismatch.' }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($extension in @('msix','zip')) {
    $archives = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.$extension")
    if ($archives.Count -ne 1) { throw "Expected exactly one $extension archive." }
    $archive = [IO.Compression.ZipFile]::OpenRead($archives[0].FullName)
    try {
        foreach ($path in $expected.Keys) {
            $entry = $archive.GetEntry($path)
            if (!$entry) { $entry = $archive.GetEntry($path.Replace('/','\')) }
            if (!$entry) { throw "Missing archive entry: $path" }
            $stream = $entry.Open(); $hash = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
            finally { $stream.Dispose(); $hash.Dispose() }
            if ($actual -ne $expected[$path].sha256) { throw "Archive checksum mismatch: $path" }
        }
    } finally { $archive.Dispose() }
}
function Invoke-Candidate([string] $Arguments, [int] $TimeoutSeconds = 60) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $payload 'FrameTrail.App.exe'; $start.WorkingDirectory = $payload
    $start.Arguments = $Arguments; $start.UseShellExecute = $false
    $start.EnvironmentVariables['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_STARTUP_HOOKS')) { $start.EnvironmentVariables.Remove($name) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); $process.WaitForExit(); throw 'Candidate validation timed out.' }
        if ($process.ExitCode -ne 0) { throw 'Candidate validation failed; inspect the result JSON.' }
    } finally { $process.Dispose() }
}
if ($SmokeTest -or $WorkflowTest) {
        $result = Join-Path $PackageDirectory 'smoke-result.json'
        Invoke-Candidate ('--smoke-test "' + $result + '"')
        $smoke = Get-Content -LiteralPath $result -Raw -Encoding UTF8 | ConvertFrom-Json
        if (!$smoke.success -or $smoke.lifecycleChecks -ne 100 -or !$smoke.runtimeDirectory.StartsWith($payload + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate did not use its local runtime or lifecycle checks failed.' }
        if ($smoke.version -ne $assemblyVersion.ToString(3)) { throw 'Native engine and application versions differ.' }
        foreach ($module in $smoke.nativeModules) { if (!$module.StartsWith($payload + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Candidate loaded a product module outside its payload.' } }
}
if ($WorkflowTest) {
    $validation = Join-Path $PackageDirectory 'workflow'
    if (Test-Path -LiteralPath $validation) { throw 'Workflow evidence already exists; retain it and choose a new candidate.' }
    $recorder = Join-Path $validation 'recorder'; $editor = Join-Path $validation 'editor'
    Invoke-Candidate ('--ui-self-test "' + $recorder + '"') 120
    $recording = Get-Content -LiteralPath (Join-Path $recorder 'ui-validation.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$recording.success) { throw 'Candidate recording workflow failed.' }
    $projects = @(Get-ChildItem -LiteralPath (Join-Path $recorder 'projects') -Directory -Filter '*.frametrailproject')
    if ($projects.Count -ne 1) { throw 'Expected one new recording project.' }
    Invoke-Candidate ('--editor "' + $projects[0].FullName + '" --m4-self-test "' + $editor + '"') 120
    $edit = Get-Content -LiteralPath (Join-Path $editor 'editor-validation.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$edit.success) { throw 'Candidate editing/export workflow failed.' }
}
Write-Host "Verified $($expected.Count) payload files, MSIX/ZIP entries and distribution checksums. SmokeTest=$SmokeTest WorkflowTest=$WorkflowTest."
