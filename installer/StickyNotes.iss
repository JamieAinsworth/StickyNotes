; Inno Setup script for Sticky Notes. Build it with: .\build.ps1 -Installer -Version 1.2.3

#ifndef AppVersion
  #define AppVersion "1.1.2"
#endif
#ifndef AppSourceDir
  #define AppSourceDir "..\artifacts\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Sticky Notes"
#define AppExe "StickyNotes.exe"

[Setup]
AppId={{6F0C2B5E-4E8A-4C55-9C0D-6A3F2E9B7D41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jamie Ainsworth
AppPublisherURL=https://github.com/JamieAinsworth/Sticky-Notes
AppSupportURL=https://github.com/JamieAinsworth/Sticky-Notes/issues
VersionInfoVersion={#AppVersion}
; Per-user install into %LOCALAPPDATA%\Programs, so no admin prompt.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Asks the user to exit the app first if it's running.
AppMutex=Local\StickyNotes_SingleInstance
OutputDir={#OutputDir}
OutputBaseFilename=StickyNotes-Setup-{#AppVersion}
SetupIconFile=..\src\StickyNotes\Assets\StickyNotes.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

// Removes the "Start with Windows" entry if it points at this install. Notes and settings in
// %APPDATA%\StickyNotes are kept, so a reinstall picks them up again.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, RunKey, 'StickyNotes', Value) then
    if Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Value)) > 0 then
      RegDeleteValue(HKCU, RunKey, 'StickyNotes');
end;
