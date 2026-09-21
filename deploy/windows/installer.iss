; ConnectionWatcher Windows installer (Inno Setup 6).
; Built by Build-Installer.ps1, which publishes conwatch.exe and
; conwatch-tray.exe into one staging folder and passes the defines below.
; Do not compile directly without them.
;
; The wizard is a thin front end over the conwatch command: every install
; and uninstall step is a `conwatch ...` call, so the zip install and this
; one behave the same. Uninstall never deletes ProgramData (data + config)
; without asking.

#ifndef AppVersion
  #define AppVersion "0.9.0"
#endif
#ifndef StagingDir
  #error Pass /DStagingDir=<path> (Build-Installer.ps1 does this)
#endif

[Setup]
AppId={{7C1B3E82-4A9D-4F1E-9C3A-5D8E2F6B7A10}
AppName=ConnectionWatcher
AppVersion={#AppVersion}
AppPublisher=DSoftCo
DefaultDirName={commonpf}\DSoftCo\ConnectionWatcher
DisableProgramGroupPage=yes
LicenseFile={#StagingDir}\LICENSE
PrivilegesRequired=admin
OutputBaseFilename=ConnectionWatcher-Setup-{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=ConnectionWatcher
CloseApplications=yes

[Tasks]
Name: "service"; Description: "Install and start the ConnectionWatcher service on this machine"
Name: "tray"; Description: "Start the tray companion when you sign in"

[Files]
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Run]
; The install steps themselves run from [Code] (CurStepChanged), because a
; plain [Run] entry cannot fail the wizard on a non-zero exit code.
Filename: "{code:DashboardUrl}"; Description: "Open the ConnectionWatcher dashboard"; \
    Flags: postinstall shellexec skipifsilent

[UninstallRun]
; "tray uninstall" is not listed here because [UninstallRun] has no
; runasoriginaluser flag (only [Run] does), and the tray autostart entry
; lives in the signed-in user's HKCU; it runs from CurUninstallStepChanged
; below via ExecAsOriginalUser instead, at the same point in the sequence.
Filename: "{app}\conwatch.exe"; Parameters: "service stop"; RunOnceId: "StopService"; Flags: runhidden waituntilterminated
Filename: "{app}\conwatch.exe"; Parameters: "service uninstall"; RunOnceId: "RemoveService"; Flags: runhidden waituntilterminated

[Code]
var
  PortPage: TInputQueryWizardPage;
  UrlPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  PortPage := CreateInputQueryPage(wpSelectTasks,
    'Dashboard port', 'Which port should the dashboard listen on?',
    'The service listens on this TCP port on every interface and the firewall is opened for it. 8080 is the usual choice.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := '8080';

  UrlPage := CreateInputQueryPage(PortPage.ID,
    'ConnectionWatcher server', 'Where is your ConnectionWatcher server?',
    'The tray companion needs the address of the machine running the service (for example http://nas:8080).');
  UrlPage.Add('Server URL:', False);
  UrlPage.Values[0] := 'http://localhost:8080';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = PortPage.ID then
    Result := not WizardIsTaskSelected('service');
  if PageID = UrlPage.ID then
    Result := WizardIsTaskSelected('service') or not WizardIsTaskSelected('tray');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    Port := StrToIntDef(PortPage.Values[0], 0);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('Enter a port between 1 and 65535.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function DashboardUrl(Param: string): string;
begin
  if WizardIsTaskSelected('service') then
    Result := 'http://localhost:' + PortPage.Values[0]
  else
    Result := UrlPage.Values[0];
end;

{ Runs conwatch with the given arguments (elevated, as the wizard is) and
  aborts the wizard with a message when it fails. Exit codes: see the
  conwatch design spec (1 usage, 2 config, 3 not installed, 4 control). }
procedure RunConwatch(const Args: string);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{app}\conwatch.exe'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('Could not start conwatch.exe ' + Args);
  if ResultCode <> 0 then
    RaiseException('conwatch ' + Args + ' failed (exit ' + IntToStr(ResultCode) + '). ' +
      'Open a terminal in ' + ExpandConstant('{app}') + ' and run: conwatch service status');
end;

{ Same, as the signed-in user: the tray autostart lives in that user's HKCU. }
procedure RunConwatchAsUser(const Args: string);
var
  ResultCode: Integer;
begin
  if not ExecAsOriginalUser(ExpandConstant('{app}\conwatch.exe'), Args, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('Could not start conwatch.exe ' + Args);
  if ResultCode <> 0 then
    RaiseException('conwatch ' + Args + ' failed (exit ' + IntToStr(ResultCode) + ').');
end;

{ ssInstall: stop what is running so files can be replaced (upgrade from the
  two-folder layout included). ssPostInstall: the install steps, in order,
  then remove the old folders — after `service install` has read the old
  contray.json from them. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM ConTray.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM conwatch-tray.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop ConnectionWatcher', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('service') then
    begin
      RunConwatch('service install --port ' + PortPage.Values[0] + ' --no-start');
      RunConwatch('service start');
    end;
    if WizardIsTaskSelected('tray') then
    begin
      if WizardIsTaskSelected('service') then
        RunConwatchAsUser('tray install')
      else
        RunConwatchAsUser('tray install --url "' + UrlPage.Values[0] + '"');
      ExecAsOriginalUser(ExpandConstant('{app}\conwatch-tray.exe'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
    DelTree(ExpandConstant('{app}\Watcher'), True, True, True);
    DelTree(ExpandConstant('{app}\Tray'), True, True, True);
    DelTree(ExpandConstant('{app}\deploy'), True, True, True);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExecAsOriginalUser(ExpandConstant('{app}\conwatch.exe'), 'tray uninstall', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Also delete the measurement data and config in' + #13#10 +
              ExpandConstant('{commonappdata}\DSoftCo\ConnectionWatcher') + '?' + #13#10#13#10 +
              'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\DSoftCo\ConnectionWatcher'), True, True, True);
  end;
end;
