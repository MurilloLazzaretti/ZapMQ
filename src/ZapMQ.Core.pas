unit ZapMQ.Core;

interface

uses
  ZapMQ.DataModule, ZapMQ.Queue;

type
  TZapCore = class
  private
    FServer : TZapDataModule;
    FQueues: TZapQueues;
    procedure SetQueues(const Value: TZapQueues);
  public
    property Queues : TZapQueues read FQueues write SetQueues;
    constructor Create; overload;
    destructor Destroy; override;
    class procedure Start;
    class procedure Stop;
  end;

  var Context : TZapCore;

implementation

{ TZapCore }

constructor TZapCore.Create;
begin
  FServer := TZapDataModule.Create(nil);
  FQueues := TZapQueues.Create;

  FServer.Server.Start;
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
