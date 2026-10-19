# Third-party notices

## Redistributed runtime dependencies

Self-contained candidates include the Microsoft .NET runtime and Windows App SDK. Their original notices are copied to `Licenses/dotnet-LICENSE.txt`, `Licenses/dotnet-NOTICES.txt` and `Licenses/WindowsAppSDK-NOTICE.txt` during packaging. The native JSON parser is nlohmann/json (MIT); its original license is included at `Licenses/nlohmann-json-MIT.txt`. The candidate's external NuGet lock file and SHA-256 payload inventory record the exact dependency versions and files. Development builds use the same dependency lock files in the repository.

The recorder and editor layouts, colour tokens and pictogram geometry are adapted from DogSC, https://github.com/laogou717/dogsc, specifically RecorderSetupSurface.swift, RecorderSurfaceStyle.swift, RecorderViews.swift, RecorderPopover.swift, RecorderPanelInteraction.swift, EditorView.swift, EditorDesignTokens.swift, EditorInspectorPalette.swift, EditorInspectorTabBar.swift, EditorBarSlider.swift, EditorTimelineLayout.swift, EditorExportViews.swift, AppSettingsView.swift and AppLineIcon.swift. The mockup turntable, cards, knobs, defaults and frame geometry additionally adapt EditorMockupTurntable.swift, EditorVisualEffectsControls.swift, EditorInspectorKnobs.swift, EditorInspectorView.swift, EditorInspectorArtwork.swift, ScreenFrameDefaults.swift, CompositionScene.swift and FrameScene.swift at revision 6df2c49b7388b753c92147a8d679ad410d3aa7dc. The WinUI and D2D implementation is maintained in FrameTrail.

The frame chrome rendering also adapts SharedFrameRenderer.swift at that same revision.

Some English UI translations adapt DogSC's Resources/en.lproj/Localizable.strings at the same revision.

MIT License

Copyright (c) 2026 神烦老狗

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.


## OpenScreen wallpapers

The 18 unmodified JPEG files in Assets/Wallpapers are from public/wallpapers in
https://github.com/siddharthvaddem/openscreen at revision
ca8104c279da0d4b4481ec992ff103949e22c772. The repository includes the MIT License,
copyright (c) 2025 Siddharth Vaddem. Its complete notice is distributed in
Licenses/openscreen-MIT.txt. Assets/Wallpapers/catalog.json records original
dimensions, byte sizes and SHA-256 checksums.

## Alibaba PuHuiTi 3.0

Unmodified Regular and SemiBold interface fonts are bundled under their original legal notice in Assets/Fonts/LICENSE.txt. They are loaded by this application and are not installed system-wide. Source: DogSC Resources/Fonts; official distribution: https://fonts.alibabadesign.com/AlibabaPuHuiTi-3.zip.

## shadcn/ui slider and switch design

The native slider and switch visuals and transitions are adapted from the shadcn/ui Slider and Switch designs: https://ui.shadcn.com/docs/components/slider and https://ui.shadcn.com/docs/components/switch. Source: https://github.com/shadcn-ui/ui (apps/v4/registry/new-york-v4/ui/slider.tsx and switch.tsx). No React or Radix runtime is bundled.

MIT License

Copyright (c) 2023 shadcn

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## WinUIEx transparency approach

RecorderTransparentBackdrop adapts the DWM alpha setup and background-clearing approach of WinUIEx TransparentTintBackdrop and CompositionBrushBackdrop: https://github.com/dotMorten/WinUIEx. FrameTrail does not bundle the WinUIEx library.

MIT License

Copyright (c) 2021 Morten Nielsen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.


## Android USB components

The optional Android runtime includes scrcpy server 3.3.4 (Genymobile and
contributors, Apache-2.0) and the ADB files from Google Android Platform-Tools
36.0.0. Their complete applicable licenses and component notices are installed
alongside the application as `android-runtime/scrcpy-LICENSE.txt` and
`android-runtime/ADB-NOTICE.txt`. No scrcpy desktop client or FFmpeg runtime is
included. The server and ADB have separate notices; neither replaces the other.
