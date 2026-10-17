[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $PackageDirectory, [Parameter(Mandatory=$true)][string] $OutputDirectory, [switch] $VersionStampedAtBuild)
. "$PSScriptRoot\common.ps1"
. "$PSScriptRoot\release-common.ps1"
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory); $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new public release directory.' }
$payload = Get-Content (Join-Path $PackageDirectory 'payload-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$release = ConvertTo-FrameTrailReleaseVersion (([version]$payload.version).ToString(3))
$name = "FrameTrail-$($release.Version)-x64"
$assets = @("$name-Setup.exe", "$name-portable.zip", 'payload-manifest.json', 'nuget-dependencies.lock.json')
if ($payload.signature -eq 'signed') { $assets += "$name.msix" }
foreach ($asset in $assets) { if (!(Test-Path -LiteralPath (Join-Path $PackageDirectory $asset))) { throw "Missing release asset: $asset" } }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
foreach ($asset in $assets) { Copy-Item -LiteralPath (Join-Path $PackageDirectory $asset) -Destination $OutputDirectory }
$instructions = @"
# FrameTrail $($release.Version)

Windows 11 x64（build 22621 或更新版本）。

- 安装版：下载 ``$name-Setup.exe``，双击安装到当前用户。安装包已包含 .NET、Windows App SDK、原生媒体引擎和 Android 组件，无需安装开发工具。
- 便携版：下载 ``$name-portable.zip``，完整解压后运行 ``FrameTrail.App.exe``。
- 更新前先保存项目并关闭全部 FrameTrail 窗口。安装器不会强制关闭录制或导出；卸载不会删除安装目录之外的工程和用户设置。
- 当前 Setup.exe 未做商业代码签名，Windows 可能显示未知发布者或 SmartScreen 提示。SHA-256 可在 ``SHA256SUMS.txt`` 中核对。
- 未签名 MSIX 不作为公众安装附件；只有带可信签名的 MSIX 才会附加到 Release。

源提交：``$($payload.sourceRevision)``。tag：``$($release.Tag)``。
这是自动构建的版本产物；长时同步、跨 GPU/DPI、Android 真机与干净机器矩阵范围见仓库 docs/todo.md。
"@
Write-FrameTrailUtf8 (Join-Path $OutputDirectory 'INSTALL.zh-CN.md') $instructions
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | ForEach-Object { [ordered]@{ name = $_.Name; bytes = $_.Length; sha256 = Get-FrameTrailSha256 $_.FullName } })
[ordered]@{ version = $release.Version; tag = $release.Tag; sourceRevision = $payload.sourceRevision; sourceDirty = [bool]$payload.sourceDirty; versionStampedAtBuild = [bool]$VersionStampedAtBuild; installerSignature = 'unsigned'; msixIncluded = ($payload.signature -eq 'signed'); files = $files } |
    ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'release-manifest.json') -Encoding UTF8
Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | ForEach-Object { (Get-FrameTrailSha256 $_.FullName) + '  ' + $_.Name } | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Release assets ready: $OutputDirectory. Not uploaded or published."
