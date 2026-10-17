#ifndef PayloadDirectory
  #error PayloadDirectory must point to the verified self-contained app directory.
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required.
#endif
#ifndef FileVersion
  #error FileVersion is required.
#endif
#ifndef PackageOutput
  #error PackageOutput is required.
#endif

[Setup]
AppId=FrameTrail.Desktop
AppName=FrameTrail
AppVersion={#ReleaseVersion}
AppPublisher=FrameTrail
AppPublisherURL=https://github.com/lucasx001/frametrail
AppSupportURL=https://github.com/lucasx001/frametrail/issues
AppUpdatesURL=https://github.com/lucasx001/frametrail/releases
VersionInfoVersion={#FileVersion}
DefaultDirName={localappdata}\Programs\FrameTrail
DefaultGroupName=FrameTrail
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22621
OutputDir={#PackageOutput}
OutputBaseFilename=FrameTrail-{#ReleaseVersion}-x64-Setup
SetupIconFile={#PayloadDirectory}\Assets\Brand\FrameTrail.Icon.v1.ico
UninstallDisplayIcon={app}\FrameTrail.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
AppMutex=Local\FrameTrail.App.Running
SetupMutex=Local\FrameTrail.Setup.Running
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\FrameTrail\FrameTrail"; Filename: "{app}\FrameTrail.App.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\FrameTrail"; Filename: "{app}\FrameTrail.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FrameTrail.App.exe"; Description: "Launch FrameTrail"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

; No UninstallDelete: projects, preferences and user presets live outside {app}.
