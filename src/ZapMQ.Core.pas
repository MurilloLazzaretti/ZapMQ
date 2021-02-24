unit ZapMQ.Core;

interface

uses
  ZapMQ.DataModule, ZapMQ.Queue;

type
  TZapCore = class
  private
    FServer : TZapDataModule;
    FQueues: TZapQueues;
    FPort: Word;
    procedure SetQueues(const Value: TZapQueues);
    procedure SetPort(const Value: Word);
    procedure LoadConfig;
  public
    property Port : Word read FPort write SetPort;
    property Queues : TZapQueues read FQueues write SetQueues;
    constructor Create; overload;
    destructor Destroy; override;
    class procedure Start;
    class procedure Stop;
  end;

  const IniFileName = 'ZapMQ.ini';
        IniSection  = 'ZapMQ';
        DefaultPort = 5679;

  var Context : TZapCore;

implementation

uses
  System.SysUtils,
  Vcl.Forms,
  Windows,
  IniFiles;

{ TZapCore }

constructor TZapCore.Create;
begin
  FServer := TZapDataModule.Create(nil);
  FQueues := TZapQueues.Create;
  LoadConfig;
  FServer.Server.Start;
  FServer.HTTPService.SessionTimeout := 1000;
  FServer.HTTPService.HttpPort := FPort;
  FServer.HTTPService.Active := True;
end;

destructor TZapCore.Destroy;
begin
  FServer.Server.Stop;
  FServer.HTTPService.Active := False;
  FServer.Free;
  FQueues.Free;
  inherited;
end;

procedure TZapCore.LoadConfig;
var
  IniFile : TIniFile;
  FileName : string;
begin
  FileName := ExtractFilePath(Application.ExeName) + IniFileName;
  if not FileExists(FileName) then
  begin
    FPort := DefaultPort;
  end
  else
  begin
    IniFile := TIniFile.Create(FileName);
    try
      FPort := IniFile.ReadInteger(IniSection, 'Port', DefaultPort);
    finally
      IniFile.Free;
    end;
  end;
end;

procedure TZapCore.SetPort(const Value: Word);
begin
  FPort := Value;
end;

procedure TZapCore.SetQueues(const Value: TZapQueues);
begin
  FQueues := Value;
end;

class procedure TZapCore.Start;
begin
  if not Assigned(Context) then
    Context := TZapCore.Create;
end;

class procedure TZapCore.Stop;
begin
  if Assigned(Context) then
    Context.Free;
end;

end.
