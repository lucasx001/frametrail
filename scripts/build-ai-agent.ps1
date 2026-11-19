[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $PythonPath = '',
    [switch] $SkipSmokeTest
)

. "$PSScriptRoot\common.ps1"

function Resolve-BasePython([string] $ExplicitPath) {
    if ($ExplicitPath) {
        if (!(Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Python executable not found at '$ExplicitPath'."
        }
        return [IO.Path]::GetFullPath($ExplicitPath)
    }

    foreach ($cmd in Get-Command python, python3 -All -ErrorAction SilentlyContinue) {
        if ($cmd.Source -and ($cmd.Source -notmatch '\\Microsoft\\WindowsApps\\')) {
            return $cmd.Source
        }
    }

    foreach ($candidate in @('C:\Python313\python.exe', 'C:\Python312\python.exe', 'C:\Python311\python.exe')) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'No usable Python 3.10+ interpreter found. Install Python or pass -PythonPath.'
}

if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $ProjectRoot 'artifacts\ai-agent-runtime'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$venvDir = Join-Path $ProjectRoot '.tools\agent-venv'
$venvPython = Join-Path $venvDir 'Scripts\python.exe'
if (!(Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    $basePython = Resolve-BasePython $PythonPath
    Write-Host "Creating isolated AI Agent build venv at $venvDir using $basePython ..."
    Invoke-Checked $basePython @('-m', 'venv', $venvDir)
}

Write-Host 'Ensuring AI Agent dependencies and PyInstaller are installed...'
Invoke-Checked $venvPython @('-m', 'pip', 'install', '--quiet', '-r', (Join-Path $ProjectRoot 'agent\requirements.txt'), 'pyinstaller>=6.10.0')

$workDir = Join-Path $ProjectRoot 'artifacts\ai-agent-build'
$distDir = Join-Path $workDir 'dist'
$buildDir = Join-Path $workDir 'build'
$specDir = Join-Path $workDir 'spec'
if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

$entryScript = Join-Path $ProjectRoot 'agent\main.py'
$pyInstallerArgs = @(
    '-m', 'PyInstaller',
    '--onedir',
    '--noconfirm',
    '--clean',
    '--name', 'frametrail-agent',
    '--distpath', $distDir,
    '--workpath', $buildDir,
    '--specpath', $specDir,
    '--paths', $ProjectRoot,
    '--hidden-import', 'agent.server',
    '--hidden-import', 'agent.graph',
    '--hidden-import', 'agent.editor_tools',
    '--hidden-import', 'agent.multimodal_tools',
    '--hidden-import', 'agent.mcp_server',
    '--hidden-import', 'agent.subscription_mcp',
    '--hidden-import', 'uvicorn.logging',
    '--hidden-import', 'uvicorn.loops',
    '--hidden-import', 'uvicorn.loops.auto',
    '--hidden-import', 'uvicorn.protocols',
    '--hidden-import', 'uvicorn.protocols.http',
    '--hidden-import', 'uvicorn.protocols.http.auto',
    '--hidden-import', 'uvicorn.protocols.websockets',
    '--hidden-import', 'uvicorn.protocols.websockets.auto',
    '--hidden-import', 'uvicorn.lifespan',
    '--hidden-import', 'uvicorn.lifespan.on',
    '--hidden-import', 'tiktoken_ext.openai_public',
    '--hidden-import', 'tiktoken_ext',
    '--collect-data', 'certifi',
    '--collect-data', 'tiktoken_ext',
    '--exclude-module', 'tkinter',
    '--exclude-module', 'matplotlib',
    '--exclude-module', 'pytest',
    $entryScript
)

Write-Host 'Building self-contained frametrail-agent.exe with PyInstaller (--onedir)...'
Push-Location $ProjectRoot
try {
    Invoke-Checked $venvPython $pyInstallerArgs
}
finally {
    Pop-Location
}

$builtBundle = Join-Path $distDir 'frametrail-agent'
$builtExe = Join-Path $builtBundle 'frametrail-agent.exe'
if (!(Test-Path -LiteralPath $builtExe -PathType Leaf)) {
    throw "PyInstaller build did not produce $builtExe"
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path (Split-Path $OutputDirectory -Parent) -Force | Out-Null
Move-Item -LiteralPath $builtBundle -Destination $OutputDirectory
Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue

$finalExe = Join-Path $OutputDirectory 'frametrail-agent.exe'
$totalMb = [Math]::Round(((Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Built self-contained AI agent runtime: $finalExe ($totalMb MB uncompressed)"

if (!$SkipSmokeTest) {
    Write-Host 'Running standalone frozen smoke test (HTTP /api/health)...'
    $testPort = 18769
    $proc = Start-Process -FilePath $finalExe -ArgumentList @('--host', '127.0.0.1', '--port', "$testPort") -PassThru -WindowStyle Hidden
    try {
        $healthy = $false
        for ($i = 0; $i -lt 40; $i++) {
            try {
                $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$testPort/api/health" -TimeoutSec 2
                if ($resp.status -eq 'ok') {
                    Write-Host "Frozen sidecar health check passed: $($resp | ConvertTo-Json -Compress)"
                    $healthy = $true
                    break
                }
            }
            catch {
                Start-Sleep -Milliseconds 250
            }
        }
        if (!$healthy) {
            throw 'Frozen frametrail-agent.exe failed /api/health smoke test.'
        }
    }
    finally {
        if ($proc -and !$proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
