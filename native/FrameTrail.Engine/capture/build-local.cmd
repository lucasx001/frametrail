@echo off
call "%LOCALAPPDATA%\FrameTrail\Toolchain\msvc\setup_x64.bat"
if not exist "%~dp0..\..\..\artifacts\capture-local" mkdir "%~dp0..\..\..\artifacts\capture-local"
pushd "%~dp0..\..\..\artifacts\capture-local"
cl /nologo /std:c++20 /EHsc /permissive- /MD /W4 /Zi /DWIN32_LEAN_AND_MEAN /DNOMINMAX /DUNICODE /D_UNICODE /DFT_CAPTURE_EXPORTS /LD "%~dp0capture.cpp" /Fe:FrameTrail.Capture.dll /link /DEBUG /MAP d3d11.lib dxgi.lib mfplat.lib mfreadwrite.lib mfuuid.lib windowsapp.lib runtimeobject.lib shcore.lib dwmapi.lib user32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++20 /EHsc /MD /W4 /DUNICODE /D_UNICODE /DNOMINMAX "%~dp0capture_smoke.cpp" FrameTrail.Capture.lib user32.lib /Fe:FrameTrail.Capture.Smoke.exe
popd
