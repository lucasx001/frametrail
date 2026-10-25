# FrameTrail user guide

For the 0.4.0 development candidate, on Windows 11 x64 (minimum build 22621).

Extract the entire portable ZIP before running `FrameTrail.App.exe`. The distribution includes its runtimes. Keep projects outside the application folder. Unsigned test MSIX files are packaging artifacts, not normal installation packages.

## Record and edit

Choose a display, window or region in the floating recorder. Clicking a display or window starts recording; drag a region within one display to select it. Enable system audio, microphone and camera separately. Check the video codec, frame rate and save location in More. Unavailable codecs are disabled. Software recording is limited to 1080p30. Android USB source recording uses H.264; phone audio requires Android 11 or later and appropriate phone authorization.

The camera menu opens a live preview. Its Mirror option affects display only; exported camera mirroring is configured separately in the editor. Preview and recording share the capture source. Recording controls support pause, resume, markers and stop. Recovery checkpoints split physical media files every 60 seconds while preserving one continuous editing clip. Pause/resume creates an intentional clip boundary.

After recording, edit clips, audio, appearance, cursor and effects. Changes save automatically. If Saved changes to Unsaved, retry saving and keep the window open. Failed saves preserve edits in memory. Wait for an export to finish or cancel it before closing.

The editor defaults to 4K composition in both windowed and fullscreen preview, preserving aspect ratio. Preview Options → Preview quality → Auto matches physical viewport pixels, up to 4K, to reduce GPU load. Preview quality does not change recorded or exported resolution.

Crop shows the full original picture. Drag its corners or move the frame; click Crop again or press Esc to return to the composed picture. Select stickers, masks and focus regions directly on the canvas to move or resize them. Releasing a gesture creates one undo step; Esc cancels it.

Hover over the timeline for a muted preview. Leaving restores the original playhead and playback state; clicking commits the new playhead. Copy an effect on the Motion tab, then paste a new effect at the playhead or paste attributes into another effect of the same kind. Attribute paste preserves the target's identity, time range, sticker content and source binding.

Thumbnails and waveforms prepare independently in the background. Audio streams from the playhead without a whole-project WAV cache. Preview Options also offers muted preview. Settings → Editor adjusts cache capacity or clears rebuildable project cache; source media and files in use are protected. Offline export still uses a temporary RIFF/RF64 PCM file.

Frame controls select window, browser and device frames. Drag the turntable, click an arrow or use Left/Right while focused. Titles and addresses appear in the exported picture. Knobs support drag, wheel, exact-value entry and Shift for fine adjustment. Each gesture is one undo step. Scene presets can be named, renamed, removed, applied or made the default for future recordings. Applying a preset preserves clips, motion effects and clip audio levels; imported backgrounds are copied into the project.

Cursor settings control the recorded pointer or built-in pointer styles, click feedback, idle hiding and timing. Positive timing values advance cursor presentation; negative values delay it without changing source events. Click-driven auto zoom is chosen before desktop recording and saved with the project; phone recordings do not support it.

Choose Canvas → Background → Image to browse 18 bundled desktop wallpapers. Click a thumbnail to apply it; the blue border marks the current selection. Import background image adds your own picture. Built-in wallpapers work offline, and the selected original is copied into the project for undo/redo, reopening, moving the project folder and export. Color, Gradient and Motion switch the background type.

## Keyboard


- Space: play/pause; Left/Right: frame step; Home/End: start/end.
- S: split; Delete: delete selected clip/effect; Ctrl+Z / Ctrl+Y: undo/redo.
- Motion: Ctrl+C copies an effect, Ctrl+V pastes at playhead, Ctrl+Shift+V pastes compatible attributes.
- F6 switches between timeline and inspector. In the timeline, Up/Down selects clips, Ctrl+Up/Down selects effects, Enter seeks to the selection.
- In the timeline, Shift+Left/Right trims the start, Ctrl+Shift+Left/Right trims the end, Alt+Left/Right reorders clips.
- F11 or double-click video toggles fullscreen. Esc cancels a gesture, leaves crop or exits fullscreen.
- Tab/Shift+Tab navigates controls. Enter confirms numeric input. Text and numeric inputs retain their standard editing shortcuts.

## Projects, recovery and permissions

Open existing project shows a searchable local project list, sorted by last saved time. Other location adds a folder; you do not need to locate `project.json` manually. Switch project saves the active project first. Cancellation or failure keeps the current editor open. The list supports arrow selection and Enter; Enter in search opens a unique usable match.

Each project is a `.frametrailproject` directory containing its media. Copy the whole directory when moving it. The default location is FrameTrail under Videos. Click the cross in the upper-right corner of a project card to open its delete confirmation without opening the project. Confirmed deletion moves the selected project to Windows Recycle Bin. The default is Cancel. Windows may ask separately about permanent deletion on unsupported locations. Active media tasks and external writer leases prevent unsafe deletion.

Recovery reopens valid completed checkpoints. Unfinished tail files remain on disk; repair is not guaranteed. Corrupted JSON may fall back to a valid previous snapshot. Future schema versions require a compatible application.

First launch opens the getting-started guide in its own window. Select Get started, close the window or press Esc to return to the recorder. After dismissal, later launches skip the guide. Settings → Recording reopens it. Settings → Permissions opens Windows microphone, camera or sound settings. After changing permissions or devices, select the device again; restart if necessary. Protected content is not guaranteed to be recordable. Memo text stays editable, selectable and scrollable; drag its title area to move the window. Closing or hiding it saves first, and failed saves keep it open.

## Language, updates and removal

Settings → General selects System, Simplified Chinese or English. The preference applies at next launch, after saving and finishing media tasks. It does not translate project names, memo text or authored sticker text.

Settings → About → Check for updates uses the configured package source for Store/MSIX installations. EXE/ZIP builds check the official GitHub stable release. If the anonymous API is limited, the official stable-release redirect can confirm the version; use the release page for notes and downloads. Network failures remain visible and never mean “up to date.” No project data is sent. Updates are installed manually: save, finish recording/exporting and exit all windows first.

Uninstall installed applications through Windows, or exit and remove the portable program directory. Projects outside the installation directory are retained. Windows may remove package-private preferences or diagnostics during MSIX uninstall.

## Validation boundaries

Local builds, automated tests, both language layouts, UI Automation, short codec/export matrices and controlled long video recording have evidence. Multi-GPU/DPI/device behavior, physical Android phones, complex audio listening, long multi-track synchronization, clean-machine installation/upgrade, Narrator and Store certification still need their respective environments. HDR capture is explicitly rejected. This development candidate has not been published as a new release.
