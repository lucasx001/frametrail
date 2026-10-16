@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-android.ps1" %*
exit /b %ERRORLEVEL%
