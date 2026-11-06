; The Windows installer. Built by tools/release.ps1 (with Inno Setup 6) from an export in dist/Grimes Golf.
; It installs per user, so it needs no administrator password and the app's folder stays writable (the tuner saves its
; presets in tuning\ beside the exe). Saved rounds, settings and downloaded courses are in %APPDATA%, and neither
; updating nor uninstalling touches them.

#ifndef AppVersion
  #error Pass the version: ISCC /DAppVersion=0.9.7 installer\GrimesGolf.iss
#endif
#define Source "..\dist\Grimes Golf"

[Setup]
; Never change AppId: it's how a new version recognises the old one and installs over it.
AppId={{6C1E2A54-8F0B-4B4D-9E3A-7D52C1F0A9B6}
AppName=Grimes Golf
AppVersion={#AppVersion}
AppVerName=Grimes Golf {#AppVersion}
AppPublisher=Grimes Golf
AppPublisherURL=https://github.com/devjared1/grimes-golf
AppUpdatesURL=https://github.com/devjared1/grimes-golf/releases
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Grimes Golf
DisableDirPage=yes
DisableProgramGroupPage=yes
SetupIconFile=..\src\GrimesGolf.Godot\assets\icons\grimes_golf.ico
UninstallDisplayIcon={app}\Grimes Golf.exe
OutputDir=..\dist
OutputBaseFilename=GrimesGolf-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Put a shortcut on the desktop"

[InstallDelete]
; Files from the version being replaced that this one might not have.
Type: filesandordirs; Name: "{app}\courses"
Type: filesandordirs; Name: "{app}\data_*"

[Files]
Source: "{#Source}\Grimes Golf.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Source}\data_GrimesGolf.Godot_windows_x86_64\*"; DestDir: "{app}\data_GrimesGolf.Godot_windows_x86_64"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Source}\courses\*"; DestDir: "{app}\courses"; Flags: ignoreversion recursesubdirs createallsubdirs
; Presets may have been changed from the tuner, so ones already there are kept.
Source: "{#Source}\tuning\*"; DestDir: "{app}\tuning"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{userprograms}\Grimes Golf"; Filename: "{app}\Grimes Golf.exe"
Name: "{userdesktop}\Grimes Golf"; Filename: "{app}\Grimes Golf.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Grimes Golf.exe"; Description: "Start Grimes Golf"; Flags: nowait postinstall skipifsilent
