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
