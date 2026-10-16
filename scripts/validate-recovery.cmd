@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate-recovery.ps1" %*
exit /b %ERRORLEVEL%
