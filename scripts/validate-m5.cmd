@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate-m5.ps1" %*
exit /b %ERRORLEVEL%
