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
    FTimeout: Word;
    FTTL: Word;
    procedure SetBody(const Value: TJSONObject);
    procedure SetId(const Value: string);
    procedure SetRPC(const Value: boolean);
    procedure SetTimeout(const Value: Word);
    procedure SetTTL(const Value: Word);
  public
    property Id : string read FId write SetId;
    property Body : TJSONObject read FBody write SetBody;
    property RPC : boolean read FRPC write SetRPC;
    property TTL : Word read FTTL write SetTTL;
    property Timeout : Word read FTimeout write SetTimeout;
    function ToJSON : TJSONObject;
    class function FromJSON(const pJSONString : string) : TZapJSONMessage;
  end;

implementation

uses
  System.SysUtils;

{ TZapJSONMessage }

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
    Result.FTimeout := JSON.GetValue<Word>('Timeout');
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

procedure TZapJSONMessage.SetRPC(const Value: boolean);
begin
  FRPC := Value;
end;

procedure TZapJSONMessage.SetTimeout(const Value: Word);
begin
  FTimeout := Value;
end;

procedure TZapJSONMessage.SetTTL(const Value: Word);
begin
  FTTL := Value;
end;

function TZapJSONMessage.ToJSON: TJSONObject;
begin
  Result := TJSONObject.Create;
  Result.AddPair('Id', TJSONString.Create(FId));
  Result.AddPair('Body', Body as TJSONValue);
  Result.AddPair('RPC', TJSONBool.Create(FRPC));
  Result.AddPair('TTL', TJSONNumber.Create(FTTL));
  Result.AddPair('Timeout', TJSONNumber.Create(FTimeout));
end;

end.
