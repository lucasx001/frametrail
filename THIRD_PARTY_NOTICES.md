# Third-party notices

## DogSC

FrameTrail's recorder/editor presentation and click-driven zoom planning use
DogSC as their reference. The C++ planner in
`native/FrameTrail.Engine/core/src/effects.cpp` adapts the grouping, timing,
activity extension and merge rules from
`Sources/RecorderCore/AutoZoomPlanner.swift`, reference revision
`6df2c49b7388b753c92147a8d679ad410d3aa7dc`.

Copyright (c) 2026 神烦老狗. Distributed under the MIT License.
The complete original notice is retained in [dogsc-MIT.txt](docs/licenses/dogsc-MIT.txt).

FrameTrail uses its own project schema, Windows UI, scene evaluator and GPU
renderer. No DogSC media, wallpaper collection or Swift runtime is bundled by
the M3 effects implementation.

## Android USB runtime (M5)

The optional Android runtime uses scrcpy **server 3.3.4**, published by
Genymobile and contributors under Apache-2.0. The complete license is retained
in [scrcpy-Apache-2.0.txt](docs/licenses/scrcpy-Apache-2.0.txt), and copied into
the application as `android-runtime/scrcpy-LICENSE.txt`.

Android SDK Platform-Tools **36.0.0** supplies `adb.exe`, `AdbWinApi.dll` and
`AdbWinUsbApi.dll`. Its own complete `NOTICE.txt`, covering its constituent
components and applicable terms, is copied from Google's pinned archive to
`android-runtime/ADB-NOTICE.txt`. The scrcpy license does not replace these
notices. Sources and checksums are recorded in
[the M5 validation record](docs/validation/0012-android-usb-validation.md).

FrameTrail does not bundle the scrcpy desktop client, SDL, FFmpeg or libusb.
H.264 decoding and MP4 packaging use Windows Media Foundation. FFmpeg in the
validation scripts is a development tool and is not copied into the application.
