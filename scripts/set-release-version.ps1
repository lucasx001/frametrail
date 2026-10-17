[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $Version, [string] $ProjectDirectory = (Split-Path $PSScriptRoot -Parent))
. "$PSScriptRoot\release-common.ps1"
Set-FrameTrailReleaseVersion $ProjectDirectory $Version
