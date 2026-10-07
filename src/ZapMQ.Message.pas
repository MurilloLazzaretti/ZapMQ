unit ZapMQ.Message;

interface

uses
  System.JSON, System.SyncObjs, ZapMQ.Message.JSON;

type
  TZapMessageStatus = (zCreated, zPending, zSended, zProcessing, zProcessed, zAnswered, zExpired);

  TZapMessage = class
  private
    FLock: TCriticalSection;
    FBirthTime: UInt64;
    FBody: TJSONObject;
    FStatus: TZapMessageStatus;
    FQueueName: string;
    FTTL: Word;
    FId: string;
    FResponse: TJSONObject;
    FRPC: Boolean;

    procedure SetBody(const Value: TJSONObject);
    procedure SetStatus(const Value: TZapMessageStatus);
    procedure SetQueueName(const Value: string);
    procedure SetTTL(const Value: Word);
    procedure SetId(const Value: string);
    procedure SetResponse(const Value: TJSONObject);
    procedure SetRPC(const Value: Boolean);
  public
    property Id: string read FId write SetId;
    property TTL: Word read FTTL write SetTTL;
    property QueueName: string read FQueueName write SetQueueName;
    property Body: TJSONObject read FBody write SetBody;
    property Status: TZapMessageStatus read FStatus write SetStatus;
    property RPC: Boolean read FRPC write SetRPC;
    property Response: TJSONObject read FResponse write SetResponse;
    property BirthTime: UInt64 read FBirthTime;
    function Prepare: TZapJSONMessage;
    procedure CheckExpiration;

    constructor Create;
    destructor Destroy; override;
  end;

implementation

uses
  System.SysUtils, System.DateUtils, Winapi.Windows;

{ TZapMessage }

constructor TZapMessage.Create;
begin
  FLock := TCriticalSection.Create;
  FStatus := zCreated;
  FBirthTime := GetTickCount64;
  FId := TGUID.NewGuid.ToString;
end;

destructor TZapMessage.Destroy;
begin
  FLock.Enter;
  try
    FreeAndNil(FBody);
    FreeAndNil(FResponse);
  finally
    FLock.Leave;
  end;
  FLock.Free;
  inherited;
end;

procedure TZapMessage.CheckExpiration;
begin
  FLock.Enter;
  try
    if (FTTL > 0) and ((GetTickCount64 - FBirthTime) > FTTL) then
      FStatus := zExpired;
  finally
    FLock.Leave;
  end;
end;

function TZapMessage.Prepare: TZapJSONMessage;
var
  NewBody, NewResp: TJSONObject;
begin
  FLock.Enter;
  try
    Result := TZapJSONMessage.Create;
    Result.Id := FId;
    NewBody := TJSONObject.ParseJSONValue(FBody.ToJSON) as TJSONObject;
    Result.Body := NewBody;
    Result.RPC := FRPC;

    if Assigned(FResponse) then
    begin
      NewResp := TJSONObject.ParseJSONValue(FResponse.ToJSON) as TJSONObject;
      Result.Response := NewResp;
    end;
  finally
    FLock.Leave;
  end;
end;

procedure TZapMessage.SetBody(const Value: TJSONObject);
begin
  FLock.Enter;
  try
    FreeAndNil(FBody);
    FBody := Value;
  finally
    FLock.Leave;
  end;
end;

procedure TZapMessage.SetId(const Value: string);
begin
  FLock.Enter;
  try
    FId := Value;
  finally
    FLock.Leave;
  end;
end;

procedure TZapMessage.SetQueueName(const Value: string);
begin
  FLock.Enter;
  try
    FQueueName := Value;
  finally
    FLock.Leave;
  end;
end;

procedure TZapMessage.SetResponse(const Value: TJSONObject);
begin
  FLock.Enter;
  try
    FreeAndNil(FResponse);
    FResponse := Value;
  finally
    FLock.Leave;
  end;
end;

procedure TZapMessage.SetRPC(const Value: Boolean);
begin
  FLock.Enter;
  try
    FRPC := Value;
  finally
    FLock.Leave;
  end;
end;

procedure TZapMessage.SetStatus(const Value: TZapMessageStatus);
begin
  FLock.Enter;
  try
    FStatus := Value;
  finally
    FLock.Leave;
  end;
end;

procedure TZapMessage.SetTTL(const Value: Word);
begin
  FLock.Enter;
  try
    FTTL := Value;
  finally
    FLock.Leave;
  end;
end;

end.

