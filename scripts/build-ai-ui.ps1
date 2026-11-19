[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $SkipInstall
)

. "$PSScriptRoot\common.ps1"

function Resolve-PnpmCommand {
    $pnpm = Get-Command pnpm.cmd -ErrorAction SilentlyContinue
    if ($pnpm) { return @{ Tool = $pnpm.Source; Prefix = @() } }

    $pnpmBin = Get-Command pnpm -ErrorAction SilentlyContinue
    if ($pnpmBin) { return @{ Tool = $pnpmBin.Source; Prefix = @() } }

    $corepack = Get-Command corepack.cmd -ErrorAction SilentlyContinue
    if (!$corepack) { $corepack = Get-Command corepack -ErrorAction SilentlyContinue }
    if ($corepack) { return @{ Tool = $corepack.Source; Prefix = @('pnpm') } }

    throw 'pnpm was not found on PATH and corepack is unavailable. Install Node.js 22+ and pnpm 10+.'
}

if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $ProjectRoot 'artifacts\ai-ui-dist'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$uiRoot = Join-Path $ProjectRoot 'src\FrameTrail.AiUi'
if (!(Test-Path -LiteralPath (Join-Path $uiRoot 'package.json') -PathType Leaf)) {
    throw "AI UI project not found at $uiRoot"
}

$pnpmRunner = Resolve-PnpmCommand

Push-Location $uiRoot
try {
    if (!$SkipInstall) {
        Write-Host 'Installing AI UI dependencies (pnpm install --frozen-lockfile)...'
        Invoke-Checked $pnpmRunner.Tool ($pnpmRunner.Prefix + @('install', '--frozen-lockfile'))
    }

    Write-Host 'Building AI UI production bundle (pnpm build)...'
    Invoke-Checked $pnpmRunner.Tool ($pnpmRunner.Prefix + @('build'))
}
finally {
    Pop-Location
}

$distDir = Join-Path $uiRoot 'dist'
$distIndex = Join-Path $distDir 'index.html'
if (!(Test-Path -LiteralPath $distIndex -PathType Leaf)) {
    throw "AI UI build did not produce $distIndex"
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $distDir '*') -Destination $OutputDirectory -Recurse -Force

$finalIndex = Join-Path $OutputDirectory 'index.html'
if (!(Test-Path -LiteralPath $finalIndex -PathType Leaf)) {
    throw "Failed to copy AI UI dist output to $OutputDirectory"
}

$totalKb = [Math]::Round(((Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1KB), 1)
Write-Host "Built AI Copilot WebView UI: $finalIndex ($totalKb KB total)"
