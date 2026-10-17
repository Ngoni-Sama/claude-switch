;
; Claude Switch - Windows installer (Inno Setup 6.3+/7). Per-user: no admin.
;
; Built by packaging/windows/build.ps1 (locally) or the release workflow:
;   ISCC /DAppVersion=2.0.0 /DSourceExe=<published ClaudeSwitch.exe> ClaudeSwitch.iss
;
; Profiles are set up inside the app on first run, the same way on every OS,
; so this only installs, adds shortcuts, and offers to start it.
;

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\..\artifacts\win-x64\ClaudeSwitch.exe"
#endif

[Setup]
; AppId uniquely identifies this app. No other app should use this ID.
AppId={{6E2B4C1A-93D7-4F25-A8E1-3C55B0F7D2A4}
AppName=Claude Switch
AppVersion={#AppVersion}
AppVerName=Claude Switch {#AppVersion}
AppPublisher=Ngoni Maphosa
AppPublisherURL=https://github.com/Ngoni-Sama/claude-switch
AppSupportURL=https://github.com/Ngoni-Sama/claude-switch/issues
AppUpdatesURL=https://github.com/Ngoni-Sama/claude-switch/releases
AppComments=Run several Claude Desktop accounts side by side. Unofficial; not affiliated with Anthropic.
AppCopyright=Copyright (c) 2026 Ngoni Maphosa
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Claude Switch Setup
DefaultDirName={autopf}\Claude Switch
DisableDirPage=yes
DefaultGroupName=Claude Switch
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\..\LICENSE
OutputDir=..\..\artifacts
OutputBaseFilename=ClaudeSwitch-{#AppVersion}-windows-x64-setup
PrivilegesRequired=lowest
Compression=lzma2/max
SolidCompression=yes
#if Ver >= EncodeVer(7, 0, 0)
WizardStyle=modern light hidebevels
WizardBackColor=#e6e9ef
#else
WizardStyle=modern
#endif
SetupIconFile=..\..\src\ClaudeSwitch\Assets\icon.ico
UninstallDisplayIcon={app}\ClaudeSwitch.exe
UninstallDisplayName=Claude Switch
CloseApplications=no
RestartIfNeededByRun=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startatlogin"; Description: "Start Claude Switch in the tray when I sign in"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "ClaudeSwitch.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\Claude Switch"; Filename: "{app}\ClaudeSwitch.exe"; Comment: "Your Claude profiles, usage and sessions"
Name: "{userdesktop}\Claude Switch"; Filename: "{app}\ClaudeSwitch.exe"; Tasks: desktopicon
; Same name the app's own "Start when I sign in" switch uses, so they agree.
Name: "{userstartup}\Claude Switch"; Filename: "{app}\ClaudeSwitch.exe"; Parameters: "--tray"; Tasks: startatlogin

[Run]
Filename: "{app}\ClaudeSwitch.exe"; Description: "Open Claude Switch"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Launchers, link registration and start-at-login. Profile data is kept.
Filename: "{app}\ClaudeSwitch.exe"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "Cleanup"

[Code]
const
  // "Claude Profiles" 1.x, the Windows-only predecessor.
  OldAppKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8C1F0B7E-2D4A-4E7B-9C35-6A0E5D2F41B9}_is1';

var
  HadDesktopProfiles: Boolean;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Uninstaller: String;
  F: TFindRec;
begin
  HadDesktopProfiles := FindFirst(ExpandConstant('{userdesktop}\Claude (*).lnk'), F);
  if HadDesktopProfiles then FindClose(F);
  // Stop a running copy (it locks the exe), then retire 1.x quietly: its silent
  // uninstall keeps every profile's data, and Claude Switch imports its list.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ClaudeSwitch.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ClaudeProfiles.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if RegQueryStringValue(HKCU, OldAppKey, 'UninstallString', Uninstaller) then
  begin
    // Keep its profiles.json + settings.json: Claude Switch migrates them on first run.
    ForceDirectories(ExpandConstant('{userappdata}\Claude Switch'));
    if not FileExists(ExpandConstant('{userappdata}\Claude Switch\profiles.json')) then
    begin
      FileCopy(ExpandConstant('{localappdata}\Programs\Claude Profiles\profiles.json'), ExpandConstant('{userappdata}\Claude Switch\profiles.json'), True);
      FileCopy(ExpandConstant('{localappdata}\Programs\Claude Profiles\settings.json'), ExpandConstant('{userappdata}\Claude Switch\settings.json'), True);
    end;
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Params: String;
begin
  // Recreate the per-profile launchers (Start menu, and Desktop if you had them).
  if CurStep = ssPostInstall then
  begin
    Params := '--refresh-shortcuts';
    if HadDesktopProfiles then Params := Params + ' --desktop';
    Exec(ExpandConstant('{app}\ClaudeSwitch.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;
