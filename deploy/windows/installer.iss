; ConnectionWatcher Windows installer (Inno Setup 6).
; Built by Build-Installer.ps1, which publishes self-contained binaries into
; the staging dir and passes the defines below. Do not compile directly
; without them.
;
; Components:
;   watcher - the monitoring service + web dashboard (default)
;   tray    - the tray-icon companion (default; installable alone on
;             machines that point at a server elsewhere)
;
; Uninstall removes the service, firewall rule, autostart, and program files,
; but never the measurement data in ProgramData - the same courtesy the
; deploy scripts extend.

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

[Types]
Name: "full"; Description: "Watcher service + tray companion"
Name: "trayonly"; Description: "Tray companion only (server runs elsewhere)"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "watcher"; Description: "Watcher service and web dashboard"; Types: full custom
Name: "tray"; Description: "Tray companion (status dot)"; Types: full trayonly custom

[Files]
Source: "{#StagingDir}\Watcher\*"; DestDir: "{app}\Watcher"; Components: watcher; \
    Flags: recursesubdirs ignoreversion
Source: "{#StagingDir}\Tray\*"; DestDir: "{app}\Tray"; Components: tray; \
    Flags: recursesubdirs ignoreversion
Source: "{#StagingDir}\deploy\*"; DestDir: "{app}\deploy"; Flags: recursesubdirs ignoreversion

[Run]
; Service install: port probe, firewall, recovery, Start Menu shortcut.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\install-service.ps1"" -AppDir ""{app}\Watcher"" -DataDir ""{commonappdata}\DSoftCo\ConnectionWatcher\data"" -NoLaunch"; \
    StatusMsg: "Installing the ConnectionWatcher service..."; \
    Components: watcher; Flags: runhidden
; Tray: contray.json + login autostart + start now. URL auto-detected from
; the service when the watcher component is present; otherwise from the
; wizard page.
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\configure-tray.ps1"" -TrayDir ""{app}\Tray""{code:TrayUrlArg}"; \
    StatusMsg: "Configuring the tray companion..."; \
    Components: tray; Flags: runhidden
Filename: "{code:DashboardUrl}"; Description: "Open the ConnectionWatcher dashboard"; \
    Flags: postinstall shellexec skipifsilent

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\deploy\uninstall-service.ps1"""; \
    RunOnceId: "RemoveService"; Flags: runhidden

[Code]
var
  UrlPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  UrlPage := CreateInputQueryPage(wpSelectComponents,
    'ConnectionWatcher server', 'Where is your ConnectionWatcher server?',
    'The tray companion needs the address of the machine running the ' +
    'Watcher service (for example http://nas:8080).');
  UrlPage.Add('Server URL:', False);
  UrlPage.Values[0] := 'http://localhost:8080';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { The URL question only matters for tray-only installs; with the service
    on this machine the port is auto-detected. }
  Result := (PageID = UrlPage.ID) and WizardIsComponentSelected('watcher');
end;

function TrayUrlArg(Param: string): string;
begin
  if WizardIsComponentSelected('watcher') then
    Result := ''
  else
    Result := ' -Url "' + UrlPage.Values[0] + '"';
end;

function DashboardUrl(Param: string): string;
begin
  if WizardIsComponentSelected('watcher') then
    Result := 'http://localhost:8080'
  else
    Result := UrlPage.Values[0];
end;
