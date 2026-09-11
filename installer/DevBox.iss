#ifndef MyAppVersion
  #define MyAppVersion "0.2.3"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef MyAppRid
  #define MyAppRid "win-x64"
#endif
#ifndef MyArchitecturesAllowed
  #define MyArchitecturesAllowed "x64compatible"
#endif
#ifndef MyArchitecturesInstallMode
  #define MyArchitecturesInstallMode "x64compatible"
#endif

#define MyAppName "DevBox Windows"
#define MyAppPublisher "DevBox"
#define MyAppExeName "DevBox.exe"

[Setup]
AppId={{C87C96C9-E130-4BB4-90A2-14F80D69B58F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\DevBox Windows
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=DevBox-{#MyAppVersion}-{#MyAppRid}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\DevBox.App\Assets\DevBox.ico
ArchitecturesAllowed={#MyArchitecturesAllowed}
ArchitecturesInstallIn64BitMode={#MyArchitecturesInstallMode}
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startmenuicon"; Description: "Create a &Start menu shortcut"; GroupDescription: "Shortcuts:"
Name: "launchafterinstall"; Description: "Launch {#MyAppName} after installation"; GroupDescription: "After installation:"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait; Tasks: launchafterinstall

; Runtime modules and temporary package data are always DevBox-owned.
; Addon project directories are removed conditionally from [Code] only when ownership
; can be established, so an unrelated www\phpmyadmin project is never deleted by name alone.
[UninstallDelete]
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\tmp\runtimes"
Type: filesandordirs; Name: "{app}\tmp\runtime-imports"
Type: filesandordirs; Name: "{app}\tmp\addons"
Type: files; Name: "{app}\config\nginx\sites-enabled\phpmyadmin.test.conf"

[Code]
const
  AppUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C87C96C9-E130-4BB4-90A2-14F80D69B58F}_is1';
  WM_CLOSE = $0010;

var
  MaintenancePage: TInputOptionWizardPage;
  ExistingInstallation: Boolean;
  InstalledLocation: String;
  InstalledVersion: String;
  InstalledUninstallString: String;
  MaintenanceExit: Boolean;

function TryReadExistingInstallation(const RootKey: Integer): Boolean;
begin
  Result := RegKeyExists(RootKey, AppUninstallKey);
  if not Result then
    Exit;

  RegQueryStringValue(RootKey, AppUninstallKey, 'InstallLocation', InstalledLocation);
  RegQueryStringValue(RootKey, AppUninstallKey, 'DisplayVersion', InstalledVersion);
  RegQueryStringValue(RootKey, AppUninstallKey, 'UninstallString', InstalledUninstallString);
end;

function DetectExistingInstallation: Boolean;
begin
  InstalledLocation := '';
  InstalledVersion := '';
  InstalledUninstallString := '';

  if IsWin64 then
  begin
    Result :=
      TryReadExistingInstallation(HKCU64) or
      TryReadExistingInstallation(HKCU32) or
      TryReadExistingInstallation(HKLM64) or
      TryReadExistingInstallation(HKLM32);
  end
  else
  begin
    Result :=
      TryReadExistingInstallation(HKCU) or
      TryReadExistingInstallation(HKLM);
  end;
end;

function ExtractExecutableFromCommand(const CommandLine: String): String;
var
  S: String;
  ClosingQuote: Integer;
  ExePos: Integer;
begin
  Result := '';
  S := Trim(CommandLine);
  if S = '' then
    Exit;

  if S[1] = '"' then
  begin
    Delete(S, 1, 1);
    ClosingQuote := Pos('"', S);
    if ClosingQuote > 0 then
      Result := Copy(S, 1, ClosingQuote - 1)
    else
      Result := S;
  end
  else
  begin
    ExePos := Pos('.exe', Lowercase(S));
    if ExePos > 0 then
      Result := Copy(S, 1, ExePos + 3)
    else
      Result := S;
  end;
end;

function ExistingUninstallerPath: String;
var
  Candidate: String;
begin
  Result := '';

  if InstalledLocation <> '' then
  begin
    Candidate := AddBackslash(InstalledLocation) + 'unins000.exe';
    if FileExists(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;
  end;

  Candidate := ExtractExecutableFromCommand(InstalledUninstallString);
  if FileExists(Candidate) then
    Result := Candidate;
end;

function IsManagedPhpMyAdmin(const RootPrefix: String): Boolean;
var
  PhpMyAdminPath: String;
  PhpMyAdminVhost: String;
  PhpMyAdminMarker: String;
  VhostContent: AnsiString;
begin
  Result := False;
  PhpMyAdminPath := RootPrefix + 'www\phpmyadmin';
  PhpMyAdminVhost := RootPrefix + 'config\nginx\sites-enabled\phpmyadmin.test.conf';
  PhpMyAdminMarker := PhpMyAdminPath + '\.devbox-addon';

  if FileExists(PhpMyAdminMarker) then
  begin
    Result := True;
    Exit;
  end;

  if not FileExists(PhpMyAdminVhost) then
    Exit;

  if not LoadStringFromFile(PhpMyAdminVhost, VhostContent) then
    Exit;

  Result :=
    (Pos('server_name phpmyadmin.test', VhostContent) > 0) and
    (Pos('root www/phpmyadmin;', VhostContent) > 0);
end;

function RemoveGeneratedModules(const BaseDir: String): Boolean;
var
  ModuleRoot: String;
  RootPrefix: String;
  PhpMyAdminPath: String;
  PhpMyAdminVhost: String;
  ManagedPhpMyAdmin: Boolean;
begin
  Result := True;
  if BaseDir = '' then
    Exit;

  ModuleRoot := RemoveBackslashUnlessRoot(BaseDir);
  RootPrefix := AddBackslash(ModuleRoot);
  PhpMyAdminPath := RootPrefix + 'www\phpmyadmin';
  PhpMyAdminVhost := RootPrefix + 'config\nginx\sites-enabled\phpmyadmin.test.conf';
  ManagedPhpMyAdmin := IsManagedPhpMyAdmin(RootPrefix);

  Log('Removing generated DevBox modules from: ' + ModuleRoot);

  DelTree(RootPrefix + 'runtime', True, True, True);
  DelTree(RootPrefix + 'tmp\runtimes', True, True, True);
  DelTree(RootPrefix + 'tmp\runtime-imports', True, True, True);
  DelTree(RootPrefix + 'tmp\addons', True, True, True);
  if ManagedPhpMyAdmin then
    DelTree(PhpMyAdminPath, True, True, True)
  else if DirExists(PhpMyAdminPath) then
    Log('Preserving www\phpmyadmin because no DevBox ownership marker or matching legacy DevBox vhost was found.');
  DeleteFile(PhpMyAdminVhost);

  Result :=
    not DirExists(RootPrefix + 'runtime') and
    not DirExists(RootPrefix + 'tmp\runtimes') and
    not DirExists(RootPrefix + 'tmp\runtime-imports') and
    not DirExists(RootPrefix + 'tmp\addons') and
    not FileExists(PhpMyAdminVhost);

  if ManagedPhpMyAdmin then
    Result := Result and not DirExists(PhpMyAdminPath);

  if not Result then
  begin
    Log('Generated module cleanup is incomplete. One or more managed module paths still exist.');
    MsgBox(
      'DevBox could not remove all downloaded modules.' + #13#10 +
      'Close processes that may still be using PHP, Nginx, MySQL or phpMyAdmin files, then run Setup again.',
      mbError, MB_OK);
  end;
end;

function RunExistingUninstaller(const SilentMode: Boolean): Boolean;
var
  Uninstaller: String;
  Params: String;
  ResultCode: Integer;
begin
  Result := False;
  Uninstaller := ExistingUninstallerPath;

  if Uninstaller = '' then
  begin
    MsgBox(
      'DevBox appears to be installed, but its uninstaller could not be found.' + #13#10 +
      'Use Windows Settings > Apps to remove the existing installation, then run this setup again.',
      mbError, MB_OK);
    Exit;
  end;

  if SilentMode then
    Params := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
  else
    Params := '/NORESTART';

  Log('Running existing DevBox uninstaller: ' + Uninstaller);
  if not Exec(Uninstaller, Params, ExtractFileDir(Uninstaller), SW_SHOWNORMAL,
    ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('The existing DevBox uninstaller could not be started.', mbError, MB_OK);
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    MsgBox(
      'The existing DevBox uninstaller returned exit code ' + IntToStr(ResultCode) + '.',
      mbError, MB_OK);
    Exit;
  end;

  Result := True;
end;

procedure InitializeWizard;
var
  InstalledText: String;
begin
  ExistingInstallation := DetectExistingInstallation;
  MaintenanceExit := False;

  if InstalledVersion <> '' then
    InstalledText := 'Installed version: ' + InstalledVersion + '. Setup version: {#MyAppVersion}.'
  else
    InstalledText := 'An existing DevBox installation was detected. Setup version: {#MyAppVersion}.';

  MaintenancePage := CreateInputOptionPage(
    wpWelcome,
    'Existing DevBox installation detected',
    InstalledText,
    'Choose how Setup should handle the existing installation, then click Next.',
    True,
    False);

  MaintenancePage.Add('&Upgrade / update - keep the installation and replace application files');
  MaintenancePage.Add('&Reinstall - remove the application and downloaded modules, then install this package');
  MaintenancePage.Add('&Uninstall - remove DevBox and exit Setup');

  if InstalledVersion = '{#MyAppVersion}' then
    MaintenancePage.SelectedValueIndex := 1
  else
    MaintenancePage.SelectedValueIndex := 0;

  if ExistingInstallation and (InstalledLocation <> '') then
    WizardForm.DirEdit.Text := InstalledLocation;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  if (MaintenancePage <> nil) and (PageID = MaintenancePage.ID) then
    Result := not ExistingInstallation;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PreviousInstallLocation: String;
begin
  Result := True;

  if (MaintenancePage = nil) or (CurPageID <> MaintenancePage.ID) then
    Exit;

  case MaintenancePage.SelectedValueIndex of
    0:
      begin
        Log('Existing installation action selected: upgrade/update.');
      end;

    1:
      begin
        Log('Existing installation action selected: reinstall.');
        PreviousInstallLocation := InstalledLocation;
        if not RunExistingUninstaller(True) then
        begin
          Result := False;
          Exit;
        end;

        { The old uninstaller may predate [UninstallDelete], therefore perform
          explicit cleanup here as well so the first reinstall also removes modules. }
        if not RemoveGeneratedModules(PreviousInstallLocation) then
        begin
          Result := False;
          Exit;
        end;

        ExistingInstallation := False;
        Log('Existing installation and generated modules removed successfully; continuing with reinstall.');
      end;

    2:
      begin
        if MsgBox(
          'DevBox will be uninstalled and this Setup will close. Continue?',
          mbConfirmation, MB_YESNO) <> IDYES then
        begin
          Result := False;
          Exit;
        end;

        Log('Existing installation action selected: uninstall.');
        PreviousInstallLocation := InstalledLocation;
        if not RunExistingUninstaller(False) then
        begin
          Result := False;
          Exit;
        end;

        if not RemoveGeneratedModules(PreviousInstallLocation) then
        begin
          MaintenanceExit := True;
          PostMessage(WizardForm.Handle, WM_CLOSE, 0, 0);
          Result := False;
          Exit;
        end;

        MaintenanceExit := True;
        MsgBox('DevBox and downloaded modules were uninstalled successfully.', mbInformation, MB_OK);
        PostMessage(WizardForm.Handle, WM_CLOSE, 0, 0);
        Result := False;
      end;
  else
    begin
      MsgBox('Choose an installation action before continuing.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveGeneratedModules(ExpandConstant('{app}'));
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if MaintenanceExit then
  begin
    Cancel := True;
    Confirm := False;
  end;
end;
