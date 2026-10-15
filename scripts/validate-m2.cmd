@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate-m2.ps1" %*
exit /b %ERRORLEVEL%
