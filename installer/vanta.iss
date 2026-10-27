; Vanta installer (Inno Setup 6). Built by the GitHub release workflow; to build locally:
;   1. build.bat           (makes publish\Vanta.exe)
;   2. iscc /DMyAppVersion=0.3.0 installer\vanta.iss
; Output: installer\Output\Vanta-Setup.exe

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyAppUrl
  #define MyAppUrl "https://github.com"
#endif

[Setup]
; Never change AppId: Windows uses it to recognise upgrades of the same app.
AppId={{6E2B8C14-7F3A-4D9B-A1C5-3E8F0B2D7A61}
AppName=Vanta
AppVersion={#MyAppVersion}
AppVerName=Vanta {#MyAppVersion}
AppPublisher=The Vanta authors
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
DefaultDirName={autopf}\Vanta
DefaultGroupName=Vanta
DisableProgramGroupPage=yes
; Vanta needs admin rights anyway, so install for all users.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=Vanta-Setup
SetupIconFile=..\Assets\vanta.ico
UninstallDisplayIcon={app}\Vanta.exe
UninstallDisplayName=Vanta
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Closes a running Vanta before replacing the EXE on upgrade.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\Vanta.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Vanta"; Filename: "{app}\Vanta.exe"
Name: "{autodesktop}\Vanta"; Filename: "{app}\Vanta.exe"; Tasks: desktopicon

[Run]
; postinstall entries run as the original (non-admin) user, and Vanta's manifest requires admin, so a
; plain CreateProcess fails with error 740. shellexec goes through ShellExecute, which shows the normal
; admin prompt instead.
Filename: "{app}\Vanta.exe"; Description: "Open Vanta now"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; The scheduled tasks point at the EXE being removed, so always delete them (harmless if absent).
Filename: "schtasks.exe"; Parameters: "/delete /tn ""Vanta\Start with Windows"" /f"; Flags: runhidden; RunOnceId: "DelStartTask"
Filename: "schtasks.exe"; Parameters: "/delete /tn ""Vanta\Weekly maintenance"" /f"; Flags: runhidden; RunOnceId: "DelMaintTask"

[Code]
var
  DidReset: Boolean;

function InitializeUninstall(): Boolean;
var
  Code: Integer;
begin
  Result := True;
  DidReset := False;
  if UninstallSilent then Exit;
  if MsgBox('Undo every change Vanta made to Windows before removing it?' + #13#10 + #13#10 +
            'This reverts all tweaks and removes the Vanta power plan, custom DNS, network adapter changes and QoS rules. ' +
            'Choose No to keep your PC exactly as it is now.',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    if Exec(ExpandConstant('{app}\Vanta.exe'), '--reset', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      DidReset := True
    else
      MsgBox('Vanta could not undo its changes automatically. You can still undo them later by reinstalling Vanta and using Settings > Reset everything.', mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Only offer to delete the data folder once the changes are undone: it holds the backups Undo needs.
  if (CurUninstallStep = usPostUninstall) and DidReset and not UninstallSilent then
    if MsgBox('Also delete Vanta''s settings, logs, backups and session reports?' + #13#10 +
              ExpandConstant('{localappdata}\Vanta'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\Vanta'), True, True, True);
end;
