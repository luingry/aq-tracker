; Per-user installer. Configuration in %APPDATA% is intentionally retained on uninstall.
#define AppName "Agent Quota Tracker"
#ifndef AppVersion
  #define AppVersion "0.8.0"
#endif
#define AppPublisher "Agent Quota Tracker"
#define AppExeName "AqTracker.exe"
; Product names used up to 0.22.x. The AppId is unchanged, so those installs upgrade in place.
#define LegacyAppName "Codex Tracker"
#define LegacyExeName "CodexTracker.exe"
[Setup]
AppId={{D8C84F82-ED90-4F1F-AB4E-1455E5B66C2C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
; Upgrades from the legacy name move to the new folder instead of reusing "Codex Tracker".
UsePreviousAppDir=no
UsePreviousGroup=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts
OutputBaseFilename=AqTracker-Setup-{#AppVersion}
SetupIconFile=..\assets\brand\aq-tracker.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
CloseApplications=yes
CloseApplicationsFilter={#AppExeName},{#LegacyExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "autostart"; Description: "Start Agent Quota Tracker when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; A self-contained predecessor may have hundreds of runtime files no longer in the
; framework-dependent payload. This runs inside {app} only; user settings stay in %APPDATA%.
[InstallDelete]
Type: filesandordirs; Name: "{app}\*"
Type: filesandordirs; Name: "{autoprograms}\{#LegacyAppName}"
Type: files; Name: "{autodesktop}\{#LegacyAppName}.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CodexTracker"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AqTracker"; ValueData: """{app}\{#AppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; The in-app updater passes /update=1 on its silent install command line so the new version
; relaunches even though skipifsilent above suppresses the interactive-only entry.
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: ShouldRelaunchAfterUpdate

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--shutdown-existing"; Flags: runhidden waituntilterminated; RunOnceId: "ShutdownAqTracker"

[Code]
var
  LegacyAppDir: String;

function ShouldRelaunchAfterUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

// The previous uninstall entry (same AppId) still points at the legacy folder before this
// install overwrites it; only a folder that really contains the legacy executable qualifies.
function InitializeSetup(): Boolean;
var
  Location: String;
  UninstallKey: String;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D8C84F82-ED90-4F1F-AB4E-1455E5B66C2C}_is1';
  if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Location) then
    RegQueryStringValue(HKLM, UninstallKey, 'InstallLocation', Location);
  if (Location <> '') and FileExists(AddBackslash(Location) + '{#LegacyExeName}') then
    LegacyAppDir := RemoveBackslash(Location);
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if LegacyAppDir = '' then Exit;
  // Graceful shutdown of a running legacy instance (it may be the one that started this update).
  Exec(AddBackslash(LegacyAppDir) + '{#LegacyExeName}', '--shutdown-existing', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if CompareText(LegacyAppDir, RemoveBackslash(ExpandConstant('{app}'))) <> 0 then
    DelTree(LegacyAppDir, True, True, True);
end;
