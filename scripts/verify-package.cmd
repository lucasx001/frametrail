@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-package.ps1" %*
exit /b %ERRORLEVEL%
