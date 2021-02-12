unit ZapMQ.Message;

interface

uses
  JSON, ZapMQ.Message.JSON;

type
  TZapMessageStatus = (zCreated, zPending, zSended, zProcessed, zProcessing,
    zAnswered, zExpired);

  TZapMessage = class
  private
    FBirthTime : Cardinal;
    FBody: TJSONObject;
    FStatus: TZapMessageStatus;
    FQueueName: string;
    FTTL: Word;
    FId: string;
    procedure SetBody(const Value: TJSONObject);
    procedure SetStatus(const Value: TZapMessageStatus);
    procedure SetQueueName(const Value: string);
    procedure SetTTL(const Value: Word);
    procedure SetId(const Value: string);
  public
    property Id : string read FId write SetId;
    property TTL : Word read FTTL write SetTTL;
    property QueueName : string read FQueueName write SetQueueName;
    property Body : TJSONObject read FBody write SetBody;
    property Status : TZapMessageStatus read FStatus write SetStatus;
    function Prepare : TZapJSONMessage;
    procedure CheckExpiration;
    constructor Create; overload;
    destructor Destroy; override;
  end;

  TZapRPCMessage = class(TZapMessage)
  private
    FResponse: TJSONObject;
    FTimeout: Word;
    procedure SetResponse(const Value: TJSONObject);
    procedure SetTimeout(const Value: Word);
  public
    property Response : TJSONObject read FResponse write SetResponse;
    property Timeout : Word read FTimeout write SetTimeout;
  end;

implementation

uses
  Windows, System.SysUtils;

{ TZapMessage }

constructor TZapMessage.Create;
begin
  FStatus := zCreated;
  FBirthTime := GetTickCount;
  FId := TGUID.NewGuid.ToString;
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

procedure TZapMessage.SetId(const Value: string);
begin
  FId := Value;
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

function TZapMessage.Prepare: TZapJSONMessage;
begin
  Result := TZapJSONMessage.Create;
  Result.Id := FId;
  Result.Body := FBody;
  Result.RPC := (Self is TZapRPCMessage);
end;

{ TZapRPCMessage }

procedure TZapRPCMessage.SetResponse(const Value: TJSONObject);
begin
  FResponse := Value;
end;

procedure TZapRPCMessage.SetTimeout(const Value: Word);
begin
  FTimeout := Value;
end;

end.
