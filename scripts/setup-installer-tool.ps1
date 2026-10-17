[CmdletBinding()]
param()
. "$PSScriptRoot\common.ps1"
$version = '7.1.0'
$expectedHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$directory = Join-Path $ProjectRoot ".tools\inno-$version"
$compiler = Join-Path $directory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
$downloads = Join-Path $ProjectRoot 'artifacts\installer-downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
$setup = Join-Path $downloads "innosetup-$version-x64.exe"
if (!(Test-Path -LiteralPath $setup)) { Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-$version-x64.exe" -OutFile $setup }
if ((Get-FrameTrailSha256 $setup) -ne $expectedHash) { throw 'Inno Setup compiler download checksum mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $setup
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') { throw 'Inno Setup publisher verification failed.' }
# Official portable mode disables registration, file associations and uninstall entries.
$process = Start-Process -FilePath $setup -WindowStyle Hidden -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/PORTABLE=1',('/DIR="' + $directory + '"')) -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Portable Inno Setup preparation failed.' }
Write-Output $compiler
