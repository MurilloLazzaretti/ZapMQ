; ZapMQ 2.x installer (Inno Setup 6.3 or newer).
;
; Build the service first, from the repository root:
;   dotnet publish src/ZapMQ.Server -c Release -r win-x64 -o publish/win-x64
; then compile this script. The executable carries the .NET runtime, so the target machine
; needs nothing installed.
;
; Installing over a 1.x (Delphi) installation replaces it: the old service is stopped and
; removed, and a port changed in ZapMQ.ini is carried over to appsettings.json.

#define MyAppName "ZapMQ"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "Murillo Lazzaretti"
#define MyAppURL "https://github.com/MurilloLazzaretti/ZapMQ"
#define MyAppExeName "ZapMQ.exe"
#define ServiceName "ZapMQ"
; Name the 1.x service was registered under.
#define LegacyServiceName "ZapMQservice"

[Setup]
; Same AppId as 1.x, so this installs over it instead of side by side.
AppId={{8ACD77CC-1398-4956-ABEF-5360AAD4993C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename=ZapMQ-{#MyAppVersion}-Installer
SetupIconFile=..\img\ZapMQ.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Settings are never overwritten by an update nor removed by the uninstaller.
Source: "..\publish\win-x64\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall; AfterInstall: CarryLegacyPortOver

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""{app}\{#MyAppExeName}"" start= auto DisplayName= ""{#MyAppName}"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""ZapMQ message broker"""; Flags: runhidden
; Restart on failure: twice after 5 s, then every minute.
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/60000"; Flags: runhidden
Filename: "{sys}\net.exe"; Parameters: "start {#ServiceName}"; Description: "Starting the ZapMQ service"; Flags: runhidden

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
procedure RunHidden(const FileName, Parameters: string);
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant(FileName), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// "net stop" waits for the service to stop, which releases the executable before the files
// are copied. Both commands simply fail when the service does not exist.
procedure RemoveService(const Name: string);
begin
  RunHidden('{sys}\net.exe', 'stop ' + Name);
  RunHidden('{sys}\sc.exe', 'delete ' + Name);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  RemoveService('{#LegacyServiceName}');
  RemoveService('{#ServiceName}');
  Result := '';
end;

// 1.x kept its only setting, the port, in ZapMQ.ini. Runs right after a fresh appsettings.json
// is installed; the ini is renamed so that this happens once.
procedure CarryLegacyPortOver;
var
  IniFile, SettingsFile: string;
  Settings: AnsiString;
  Text: string;
  Port: Integer;
begin
  IniFile := ExpandConstant('{app}\ZapMQ.ini');
  SettingsFile := ExpandConstant('{app}\appsettings.json');
  if not FileExists(IniFile) then
    Exit;

  Port := GetIniInt('ZapMQ', 'Port', 5679, 1, 65535, IniFile);
  if (Port <> 5679) and LoadStringFromFile(SettingsFile, Settings) then
  begin
    Text := String(Settings);
    if StringChangeEx(Text, '"Port": 5679', '"Port": ' + IntToStr(Port), True) > 0 then
      SaveStringToFile(SettingsFile, AnsiString(Text), False);
  end;

  RenameFile(IniFile, IniFile + '.1x');
end;
