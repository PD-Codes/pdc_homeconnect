; PDC HomeConnect - Windows installer (Inno Setup 6)
; Build: client\build.ps1 (publishes the apps to artifacts\publish and compiles this script).
; Silent install/update: PDC-HomeConnect-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-

#define AppName "PDC HomeConnect"
#define ServiceName "PdcHomeConnect"
#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{6F1C2B7E-4D0B-4C55-9B3A-2E7D5A1C9F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=PD-Codes
AppPublisherURL=https://github.com/PD-Codes/pdc_homeconnect
AppSupportURL=https://github.com/PD-Codes/pdc_homeconnect/issues
AppUpdatesURL=https://github.com/PD-Codes/pdc_homeconnect/releases
DefaultDirName={autopf}\PDC HomeConnect
DisableProgramGroupPage=yes
DisableReadyMemo=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=PDC-HomeConnect-Setup-{#AppVersion}
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\PdcHomeConnect.Agent.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany=PD-Codes
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "pawnio"; Description: "Install the PawnIO driver (CPU temperature, fans and drive temperatures; recommended)"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
; Tray app for every user, started at sign-in (no console window, lives in the notification area).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PDC HomeConnect"; ValueData: """{app}\PdcHomeConnect.Agent.exe"" --autostart"; Flags: uninsdeletevalue

[Run]
; Start the tray app for the user running the setup (silent updates: the service starts it in each session).
Filename: "{app}\PdcHomeConnect.Agent.exe"; Parameters: "--autostart"; Flags: nowait runasoriginaluser; Check: not WizardSilent

[UninstallRun]
; No automatic restart while uninstalling (a forced stop would count as a crash).
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 0 actions= """""; Flags: runhidden waituntilterminated; RunOnceId: "NoRecovery"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PdcHomeConnect.Service.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PdcHomeConnect.Agent.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillAgents"
Filename: "{app}\PdcHomeConnect.Agent.exe"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "AgentCleanup"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PDC HomeConnect (Home Assistant)"""; Flags: runhidden waituntilterminated; RunOnceId: "FirewallTcp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PDC HomeConnect (mDNS discovery)"""; Flags: runhidden waituntilterminated; RunOnceId: "FirewallMdns"

[Code]
const
  PawnIOUrl = 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe';

function ExecHidden(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Log(Format('%s %s -> %d', [FileName, Params, ResultCode]));
  Result := ResultCode;
end;

function ServiceExists(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\{#ServiceName}');
end;

function PawnIOInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

{ Stop the running service and tray apps so their files can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if ServiceExists() then
  begin
    { A forced stop must not trigger the automatic restart (re-enabled in RegisterService). }
    ExecHidden(ExpandConstant('{sys}\sc.exe'), 'failure {#ServiceName} reset= 0 actions= ""');
    ExecHidden(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  end;
  ExecHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PdcHomeConnect.Service.exe');
  ExecHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM PdcHomeConnect.Agent.exe');
  ExecHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM pdc-hc-hook.exe');
  Sleep(2000);
  Result := '';
end;

procedure InstallPawnIO();
begin
  if PawnIOInstalled() then
  begin
    Log('PawnIO is already installed');
    exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver...';
  try
    DownloadTemporaryFile(PawnIOUrl, 'PawnIO_setup.exe', '', nil);
    ExecHidden(ExpandConstant('{tmp}\PawnIO_setup.exe'), '-install -silent');
  except
    Log('PawnIO download failed: ' + GetExceptionMessage);
    SuppressibleMsgBox('The PawnIO driver could not be downloaded. CPU temperatures and fan speeds may be unavailable.' + #13#10 + #13#10 +
      'You can install it later from https://pawnio.eu', mbInformation, MB_OK, IDOK);
  end;
end;

procedure RegisterService();
var
  Sc, BinPath: String;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  { The quotes inside binPath prevent the "unquoted service path" problem. }
  BinPath := 'binPath= "\"' + ExpandConstant('{app}') + '\PdcHomeConnect.Service.exe\""';
  if ServiceExists() then
    ExecHidden(Sc, 'config {#ServiceName} ' + BinPath + ' start= delayed-auto obj= LocalSystem DisplayName= "PDC HomeConnect"')
  else
    ExecHidden(Sc, 'create {#ServiceName} ' + BinPath + ' start= delayed-auto obj= LocalSystem DisplayName= "PDC HomeConnect"');

  ExecHidden(Sc, 'description {#ServiceName} "Connects this PC with Home Assistant (PDC HomeConnect)."');
  { Restart after crashes and after config changes (the service exits with code 1 to restart itself). }
  ExecHidden(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000');
  ExecHidden(Sc, 'failureflag {#ServiceName} 1');
  ExecHidden(Sc, 'start {#ServiceName}');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('pawnio') then
      InstallPawnIO();
    WizardForm.StatusLabel.Caption := 'Starting the PDC HomeConnect service...';
    RegisterService();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent() then
    begin
      if MsgBox('Also delete the settings and Home Assistant pairings of PDC HomeConnect?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(ExpandConstant('{commonappdata}\PDC\HomeConnect'), True, True, True);
        DelTree(ExpandConstant('{userappdata}\PDC\HomeConnect'), True, True, True);
        DelTree(ExpandConstant('{localappdata}\PDC\HomeConnect'), True, True, True);
      end;
    end;
  end;
end;
