unit ZapMQ.Methods;

interface

uses
  System.Classes,
  JSON;

type
{$METHODINFO ON}
  TZapMethods = class(TComponent)
  public
    function GetMessage(const pQueueName : string) : string;
    function UpdateMessage(const pQueueName : string;
      pMessage : string; const pTTL : Word = 0) : TJSONValue;
  end;
{$METHODINFO OFF}

implementation

uses
  ZapMQ.Core, ZapMQ.Message, ZapMQ.Queue, System.SysUtils;

{ TZapMethods }

function TZapMethods.GetMessage(const pQueueName: string): string;
var
  Queue : TZapQueue;
  ZapMessage : TZapMessage;
begin
  Result := '';
  Queue := ZapMQ.Core.Context.Queues.Find(pQueueName);
  if Assigned(Queue) then
  begin
    ZapMessage := Queue.GetMessage;
    if Assigned(ZapMessage) then
    begin
      ZapMessage.Status := zProcessed;
      Result := ZapMessage.Body.ToString;
    end;
  end;
end;

function TZapMethods.UpdateMessage(const pQueueName: string;
  pMessage: string; const pTTL: Word): TJSONValue;
var
  ZapMessage : TZapMessage;
begin
  Result := nil;
  ZapMessage := TZapMessage.Create;
  ZapMessage.QueueName := pQueueName;
  ZapMessage.TTL := pTTL;
  ZapMessage.Body := TJSONObject.ParseJSONValue(TEncoding.ASCII.GetBytes(pMessage), 0) as TJSONObject;

  ZapMQ.Core.Context.Queues.AddMessage(ZapMessage);
end;

end.
