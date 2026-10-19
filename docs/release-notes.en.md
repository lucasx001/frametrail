# 0.4.0 development candidate

2026-10-19: Added 18 desktop wallpapers from OpenScreen under Canvas → Background → Image, with thumbnail selection and custom image import. Selected originals travel with the project and support undo/redo. Large images use bounded decoding for shared preview/export rendering.

FrameTrail provides Windows-native recording, non-destructive editing, motion effects, preview and MP4/M4A export. Android USB and distribution tooling have local implementation and automated evidence; hardware and release acceptance remain separate.

2026-10-19: Fixed first-use guidance being clipped inside the recorder capsule. The guide now has its own window with a fixed bottom action; closing it or pressing Esc returns to the recorder.

P1 adds direct canvas/crop editing, shared camera preview, streaming audio playback, RF64 support and cache controls. Continuous recording checkpoints retain one logical editing clip.

P2 adds muted hover browsing with transport restoration, coalesced seeking, compatible attribute paste, timeline keyboard/automation semantics, interval indexing, batched drawing, virtual project rows and debounced search. Compatible exports use an encoder-owned GPU surface pool with a CPU fallback. English and Simplified Chinese resources, language preference, first-use guidance, permission links and official update checks are included.

Local build/test and application/media validation do not replace physical Android, cross-GPU/DPI/peripheral, multi-track long-duration sync, Narrator or clean-machine installation/upgrade testing. Store identity, installation, WACK and certification remain pending. This candidate has not been installed or published by the P2 implementation task.
