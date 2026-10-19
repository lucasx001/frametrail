# Privacy and permissions

FrameTrail stores recordings, audio, cursor events and edits locally. It has no account, cloud upload, AI service or telemetry. Choose your screen/window and audio sources before recording. Camera and microphone are used only after selection and enabling.

Cursor events support cursor presentation and camera motion; they do not log typed text. Recorded video and audio contain whatever appears in the selected sources. Choose where to save and share them.

Crash diagnostics contain application/OS versions, exception types, HRESULTs and method names without file paths. At most ten new reports are retained. Exception messages, command lines, usernames, projects, screenshots, device serial numbers and media paths are excluded. Older raw crash logs may remain locally but are not included in new diagnostic exports.

Settings → About → Export diagnostics writes reports only to a location you select. Reports are never sent automatically. Development validation scripts may produce reports containing local paths; review them before sharing.

Clicking Check for updates queries the configured Store/MSIX package source, or the official FrameTrail GitHub release API for EXE/ZIP builds. An API quota limit may trigger a request to the official stable-release page. These requests expose normal network information to the source but do not send project contents. No automatic installation takes place.

Projects live outside the installation directory. Removing a portable app means deleting its program directory after exiting. MSIX removal may clear package-private preferences and diagnostics, but should retain projects in user media folders. Cross-machine installation, upgrade and removal remain separately validated tasks.
