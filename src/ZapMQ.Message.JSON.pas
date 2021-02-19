unit ZapMQ.Message.JSON;

interface

uses
  JSON;

type
  TZapJSONMessage = class
  private
    FBody: TJSONObject;
    FId: string;
    FRPC: boolean;
    FTTL: Word;
    FResponse: TJSONObject;
    procedure SetBody(const Value: TJSONObject);
    procedure SetId(const Value: string);
    procedure SetRPC(const Value: boolean);
    procedure SetTTL(const Value: Word);
    procedure SetResponse(const Value: TJSONObject);
  public
    property Id : string read FId write SetId;
    property Body : TJSONObject read FBody write SetBody;
    property RPC : boolean read FRPC write SetRPC;
    property TTL : Word read FTTL write SetTTL;
    property Response : TJSONObject read FResponse write SetResponse;
    function ToJSON : TJSONObject;
    destructor Destroy; override;
    class function FromJSON(const pJSONString : string) : TZapJSONMessage;
  end;

implementation

uses
  System.SysUtils;

{ TZapJSONMessage }

destructor TZapJSONMessage.Destroy;
begin
  if Assigned(Body) then
    Body.Free;
  if Assigned(Response) then
    Response.Free;
  inherited;
end;

class function TZapJSONMessage.FromJSON(
  const pJSONString: string): TZapJSONMessage;
var
  JSON : TJSONObject;
begin
  JSON := TJSONObject.ParseJSONValue(
    TEncoding.ASCII.GetBytes(pJSONString), 0) as TJSONObject;
  try
    Result := TZapJSONMessage.Create;
    Result.FId := JSON.GetValue<string>('Id');
    Result.FBody := TJSONObject.ParseJSONValue(
      TEncoding.ASCII.GetBytes(JSON.GetValue<TJSONObject>('Body').ToString), 0) as TJSONObject;
    Result.FRPC := JSON.GetValue<boolean>('RPC');
    Result.FTTL := JSON.GetValue<Word>('TTL');
  finally
    JSON.Free;
  end;
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

procedure TZapJSONMessage.SetRPC(const Value: boolean);
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
  Result.AddPair('Id', TJSONString.Create(FId));
  Result.AddPair('Body', TJSONObject.ParseJSONValue(
    TEncoding.ASCII.GetBytes(Body.ToString), 0) as TJSONValue);
  Result.AddPair('RPC', TJSONBool.Create(FRPC));
  Result.AddPair('TTL', TJSONNumber.Create(FTTL));
  Result.AddPair('Response', TJSONObject.ParseJSONValue(
    TEncoding.ASCII.GetBytes(Response.ToString), 0) as TJSONValue);
end;

end.
