; Setup.exe for WatchFlix.
;
; Built automatically by the GitHub release workflow. To build it by hand,
; publish the app into ..\publish first, then open this file in Inno Setup 6
; and press Compile.
;
; Installs per user, so Windows never shows an administrator prompt.

#define AppName      "WatchFlix"
#define AppPublisher "Fwoce Media"
#define AppExe       "WatchFlix.exe"
#ifndef AppVersion
  #define AppVersion "4.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{73C939F6-68C8-457B-BE0C-41913D3DA53A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://fwoce-media.github.io/
AppSupportURL=https://github.com/Fwoce-Media/watchflix/issues
DefaultDirName={autopf}\WatchFlix
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=WatchFlix-{#AppVersion}-Setup
SetupIconFile=..\src\WatchFlix.Desktop\watchflix.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only what setup put here. The library lives in the user's profile and stays.
Type: filesandordirs; Name: "{app}\libvlc"
