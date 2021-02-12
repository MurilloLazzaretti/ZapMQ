unit ZapMQ.Message;

interface

uses
  JSON, Vcl.ExtCtrls;

type
  TZapMessageStatus = (zCreated, zPending, zProcessed, zProcessedError, zExpired);

  TZapMessage = class
  private
    FBirthTime : Cardinal;
    FBody: TJSONObject;
    FStatus: TZapMessageStatus;
    FQueueName: string;
    FTTL: Word;
    procedure SetBody(const Value: TJSONObject);
    procedure SetStatus(const Value: TZapMessageStatus);
    procedure SetQueueName(const Value: string);
    procedure SetTTL(const Value: Word);
  public
    property TTL : Word read FTTL write SetTTL;
    property QueueName : string read FQueueName write SetQueueName;
    property Body : TJSONObject read FBody write SetBody;
    property Status : TZapMessageStatus read FStatus write SetStatus;
    procedure CheckExpiration;
    constructor Create; overload;
    destructor Destroy; override;
  end;

implementation

uses
  Windows;

{ TZapMessage }

constructor TZapMessage.Create;
begin
  FStatus := zCreated;
  FBirthTime := GetTickCount;
end;

destructor TZapMessage.Destroy;
begin
  if Assigned(FBody) then
    FBody.Free;
  inherited;
end;

procedure TZapMessage.CheckExpiration;
begin
  if FTTL > 0 then
    if (FBirthTime + FTTL) < GetTickCount then
      FStatus := zExpired;
end;

procedure TZapMessage.SetBody(const Value: TJSONObject);
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

procedure TZapMessage.SetTTL(const Value: Word);
begin
  FTTL := Value;
end;

end.
