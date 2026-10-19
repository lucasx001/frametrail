@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate-wallpapers.ps1" %*
