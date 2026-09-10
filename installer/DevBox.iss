#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
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
OutputBaseFilename=DevBox-{#MyAppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\DevBox.App\Assets\DevBox.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
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
  MaintenancePage.Add('&Reinstall - remove the installed application first, then install this package');
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
        if not RunExistingUninstaller(True) then
        begin
          Result := False;
          Exit;
        end;

        ExistingInstallation := False;
        Log('Existing installation removed successfully; continuing with reinstall.');
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
        if not RunExistingUninstaller(False) then
        begin
          Result := False;
          Exit;
        end;

        MaintenanceExit := True;
        MsgBox('DevBox was uninstalled successfully.', mbInformation, MB_OK);
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

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if MaintenanceExit then
  begin
    Cancel := True;
    Confirm := False;
  end;
end;
