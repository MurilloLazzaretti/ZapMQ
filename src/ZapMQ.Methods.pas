unit ZapMQ.Methods;

interface

uses
  System.Classes,
  JSON;

type
{$METHODINFO ON}
  TZapMethods = class(TComponent)
  public
    function GetMessage(const pQueueName : string) : TJSONValue;
    function UpdateMessage(const pQueueName : string; const pTTL : Word = 0) : TJSONValue;
  end;
{$METHODINFO OFF}

implementation

uses
  ZapMQ.Core, ZapMQ.Message, ZapMQ.Queue, System.SysUtils, Datasnap.DSHTTPWebBroker;

{ TZapMethods }

function TZapMethods.GetMessage(const pQueueName: string): TJSONValue;
var
  Queue : TZapQueue;
  ZapMessage : TZapMessage;
begin
  Result := nil;
  Queue := ZapMQ.Core.Context.Queues.Find(pQueueName);
  if Assigned(Queue) then
  begin
    ZapMessage := Queue.GetMessage;
    if Assigned(ZapMessage) then
    begin
      ZapMessage.Status := zProcessed;
      Result := ZapMessage.Body
    end;
  end;
end;

function TZapMethods.UpdateMessage(const pQueueName: string;
  const pTTL: Word): TJSONValue;
var
  ZapMessage : TZapMessage;
begin
  Result := nil;
  ZapMessage := TZapMessage.Create(pTTL);
  ZapMessage.QueueName := pQueueName;
  ZapMessage.Body := TJSONObject.ParseJSONValue(
    TEncoding.UTF8.GetBytes(GetDataSnapWebModule.Request.Content), 0);

  ZapMQ.Core.Context.Queues.AddMessage(ZapMessage);
end;

end.
