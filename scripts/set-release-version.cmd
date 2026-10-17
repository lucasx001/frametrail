@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-release-version.ps1" %*
