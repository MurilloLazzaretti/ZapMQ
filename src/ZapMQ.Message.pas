unit ZapMQ.Message;

interface

uses
  JSON, Vcl.ExtCtrls;

type
  TZapMessageStatus = (zCreated, zPending, zProcessed, zProcessedError, zExpired);

  TZapMessage = class
  private
    FTimer : TTimer;
    FBody: TJSONValue;
    FStatus: TZapMessageStatus;
    FQueueName: string;
    procedure SetBody(const Value: TJSONValue);
    procedure SetStatus(const Value: TZapMessageStatus);
    procedure OnTTL(Sender: TObject);
    procedure SetQueueName(const Value: string);
  public
    property QueueName : string read FQueueName write SetQueueName;
    property Body : TJSONValue read FBody write SetBody;
    property Status : TZapMessageStatus read FStatus write SetStatus;
    constructor Create(const pTTL : word = 0); overload;
    destructor Destroy; override;
  end;

implementation

{ TZapMessage }

constructor TZapMessage.Create(const pTTL: word);
begin
  FStatus := zCreated;
  if pTTL > 0 then
  begin
    FTimer := TTimer.Create(nil);
    FTimer.Interval := pTTL;
    FTimer.OnTimer := OnTTL;
    FTimer.Enabled := True;
  end;
end;

destructor TZapMessage.Destroy;
begin
  if Assigned(FTimer) then
  begin
    FTimer.Enabled := False;
    FTimer.Free;
  end;
  if Assigned(FBody) then
  begin
    FBody.Free;
  end;
  inherited;
end;

procedure TZapMessage.OnTTL(Sender: TObject);
begin
  FStatus := zExpired;
end;

procedure TZapMessage.SetBody(const Value: TJSONValue);
begin
  FBody := Value;
end;

procedure TZapMessage.SetQueueName(const Value: string);
begin
  FQueueName := Value;
end;

procedure TZapMessage.SetStatus(const Value: TZapMessageStatus);
begin
  FStatus := Value;
end;

end.
