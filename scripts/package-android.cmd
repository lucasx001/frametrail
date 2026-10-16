@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package-android.ps1" %*
exit /b %ERRORLEVEL%
