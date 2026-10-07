unit ZapMQ.Message.JSON;

interface

uses
  System.JSON;

type
  TZapJSONMessage = class
  private
    FBody: TJSONObject;
    FId: string;
    FRPC: Boolean;
    FTTL: Word;
    FResponse: TJSONObject;
    procedure SetBody(const Value: TJSONObject);
    procedure SetId(const Value: string);
    procedure SetRPC(const Value: Boolean);
    procedure SetTTL(const Value: Word);
    procedure SetResponse(const Value: TJSONObject);
  public
    property Id: string read FId write SetId;
    property Body: TJSONObject read FBody write SetBody;
    property RPC: Boolean read FRPC write SetRPC;
    property TTL: Word read FTTL write SetTTL;
    property Response: TJSONObject read FResponse write SetResponse;

    function ToJSON: TJSONObject;
    class function FromJSON(const pJSONString: string): TZapJSONMessage;

    destructor Destroy; override;
  end;

implementation

uses
  System.SysUtils;

{ TZapJSONMessage }

destructor TZapJSONMessage.Destroy;
begin
  FBody.Free;
  FResponse.Free;
  inherited;
end;

procedure TZapJSONMessage.SetBody(const Value: TJSONObject);
begin
  FBody := Value;
end;

procedure TZapJSONMessage.SetId(const Value: string);
begin
  FId := Value;
end;

procedure TZapJSONMessage.SetResponse(const Value: TJSONObject);
begin
  FResponse := Value;
end;

procedure TZapJSONMessage.SetRPC(const Value: Boolean);
begin
  FRPC := Value;
end;

procedure TZapJSONMessage.SetTTL(const Value: Word);
begin
  FTTL := Value;
end;

function TZapJSONMessage.ToJSON: TJSONObject;
begin
  Result := TJSONObject.Create;
  Result.AddPair('Id', FId);

  if Assigned(FBody) then
    Result.AddPair('Body', FBody.Clone as TJSONObject)
  else
    Result.AddPair('Body', TJSONObject.Create);

  Result.AddPair('RPC', TJSONBool.Create(FRPC));
  Result.AddPair('TTL', TJSONNumber.Create(FTTL));

  if Assigned(FResponse) then
    Result.AddPair('Response', FResponse.Clone as TJSONObject)
  else
    Result.AddPair('Response', TJSONObject.Create);
end;

class function TZapJSONMessage.FromJSON(const pJSONString: string): TZapJSONMessage;
var
  JSON: TJSONObject;
  BodyValue, RespValue: TJSONValue;
begin
  Result := TZapJSONMessage.Create;

  JSON := TJSONObject.ParseJSONValue(pJSONString) as TJSONObject;
  if not Assigned(JSON) then
    raise Exception.Create('Invalid JSON format');

  try
    Result.FId := JSON.GetValue<string>('Id');
    Result.FRPC := JSON.GetValue<Boolean>('RPC');
    Result.FTTL := JSON.GetValue<Word>('TTL');

    BodyValue := JSON.GetValue('Body');
    if Assigned(BodyValue) and (BodyValue is TJSONObject) then
      Result.FBody := (BodyValue as TJSONObject).Clone as TJSONObject
    else
      Result.FBody := TJSONObject.Create;

    RespValue := JSON.GetValue('Response');
    if Assigned(RespValue) and (RespValue is TJSONObject) then
      Result.FResponse := (RespValue as TJSONObject).Clone as TJSONObject
    else
      Result.FResponse := TJSONObject.Create;
  finally
    JSON.Free;
  end;
end;

end.

