@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate-m3.ps1" %*
exit /b %errorlevel%
