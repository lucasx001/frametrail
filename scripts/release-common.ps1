Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-FrameTrailReleaseVersion([string] $Value) {
    if ($Value -notmatch '^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Version must be X.Y.Z or vX.Y.Z, for example 0.0.1. Prerelease suffixes and leading zeros are not supported.' }
    $parts = @($Matches[1], $Matches[2], $Matches[3])
    # .NET assembly metadata reserves UInt16.MaxValue; use the shared lower limit.
    foreach ($part in $parts) { $number = 0; if (![int]::TryParse($part, [ref]$number) -or $number -gt 65534) { throw 'Each version component must be 0..65534.' } }
    $version = $parts -join '.'
    if ($version -eq '0.0.0') { throw 'The first release version must be greater than 0.0.0.' }
    [pscustomobject]@{ Version = $version; FileVersion = "$version.0"; Tag = "v$version" }
}

function Write-FrameTrailUtf8([string] $Path, [string] $Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

function Get-FrameTrailReleaseHash([string] $Path) {
    $hash = [Security.Cryptography.SHA256]::Create(); $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
}

function Test-FrameTrailReleaseAssets([string] $Directory, [string] $Version, [string] $ExpectedCommit) {
    $release = ConvertTo-FrameTrailReleaseVersion $Version
    $manifest = Get-Content -LiteralPath (Join-Path $Directory 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.version -ne $release.Version -or $manifest.tag -ne $release.Tag -or $manifest.sourceRevision -ne $ExpectedCommit) { throw 'Release metadata differs from the requested version/source commit.' }
    $baseName = "FrameTrail-$($release.Version)-x64"
    $requiredNames = @("$baseName-Setup.exe", "$baseName-portable.zip", 'payload-manifest.json', 'nuget-dependencies.lock.json', 'INSTALL.zh-CN.md')
    if ($manifest.msixIncluded) { $requiredNames += "$baseName.msix" }
    if (@($manifest.files).Count -ne $requiredNames.Count) { throw 'Unexpected release manifest asset count.' }
    $inventory = @{}
    foreach ($file in $manifest.files) {
        if ($file.name -notin $requiredNames -or $inventory.ContainsKey($file.name)) { throw 'Invalid or duplicate release manifest asset.' }
        $path = Join-Path $Directory $file.name
        if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FrameTrailReleaseHash $path) -ne $file.sha256) { throw "Release inventory mismatch: $($file.name)" }
        $inventory[$file.name] = $true
    }
    $expectedNames = $requiredNames + @('release-manifest.json')
    $seen = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt')) {
        if ($line -notmatch '^([a-f0-9]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Invalid release checksum entry.' }
        $hash = $Matches[1]; $name = $Matches[2]
        if ($seen.ContainsKey($name) -or $name -notin $expectedNames) { throw 'Unexpected or duplicate release asset.' }
        if ((Get-FrameTrailReleaseHash (Join-Path $Directory $name)) -ne $hash) { throw "Release asset checksum mismatch: $name" }
        $seen[$name] = $true
    }
    if ($seen.Count -ne $expectedNames.Count -or @(Get-ChildItem -LiteralPath $Directory).Count -ne $seen.Count + 1) { throw 'Release asset set is incomplete or contains extra files.' }
}

function Set-FrameTrailReleaseVersion([string] $ProjectDirectory, [string] $Version) {
    $release = ConvertTo-FrameTrailReleaseVersion $Version
    $root = [IO.Path]::GetFullPath($ProjectDirectory)
    $files = @('version.props', 'src\FrameTrail.App\app.manifest', 'packaging\AppxManifest.xml')
    foreach ($relative in $files) { if (!(Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw "Missing version source: $relative" } }
    $propsPath = Join-Path $root 'version.props'
    [xml] $props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
    $props.Project.PropertyGroup.Version = $release.Version
    $props.Project.PropertyGroup.AssemblyVersion = $release.FileVersion
    $props.Project.PropertyGroup.FileVersion = $release.FileVersion
    $props.Save($propsPath)
    $appPath = Join-Path $root 'src\FrameTrail.App\app.manifest'
    [xml] $app = Get-Content -LiteralPath $appPath -Raw -Encoding UTF8
    $app.assembly.assemblyIdentity.version = $release.FileVersion; $app.Save($appPath)
    $msixPath = Join-Path $root 'packaging\AppxManifest.xml'
    [xml] $msix = Get-Content -LiteralPath $msixPath -Raw -Encoding UTF8
    $msix.Package.Identity.Version = $release.FileVersion; $msix.Save($msixPath)
    # NuGet records local project versions in lock files. Update only these edges;
    # external dependencies, content hashes and resolved package versions stay pinned.
    foreach ($folder in @('src','tests')) {
        foreach ($lockFile in Get-ChildItem -LiteralPath (Join-Path $root $folder) -Filter packages.lock.json -Recurse) {
            $lock = Get-Content -LiteralPath $lockFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($framework in $lock.dependencies.PSObject.Properties) {
                foreach ($dependency in $framework.Value.PSObject.Properties) {
                    $edges = $dependency.Value.PSObject.Properties['dependencies']
                    if (!$edges) { continue }
                    foreach ($edge in $edges.Value.PSObject.Properties) {
                        if ($edge.Name -like 'FrameTrail.*') { $edge.Value = "[$($release.Version), )" }
                    }
                }
            }
            Write-FrameTrailUtf8 $lockFile.FullName ($lock | ConvertTo-Json -Depth 100)
        }
    }
    return $release
}
