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
    function UpdateMessage(const pQueueName : string; pMessage : string) : string;
    function UpdateRPCResponse(const pQueueName : string;
      const pIdMessage : string; pResponse : string) : string;
    function GetRPCResponse(const pQueueName : string; const pIdMessage : string) : string;
  end;
{$METHODINFO OFF}

implementation

uses
  ZapMQ.Core, ZapMQ.Message, ZapMQ.Queue, ZapMQ.Message.JSON, System.SysUtils;

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
    ZapMessage := Queue.GetNextMessageToProcess;
    if Assigned(ZapMessage) then
    begin
      Result := ZapMessage.Prepare.ToJSON.ToString;
      ZapMessage.Status := zSended;
    end;
  end;
end;

function TZapMethods.GetRPCResponse(const pQueueName,
  pIdMessage: string): string;
var
  Queue : TZapQueue;
  ZapMessage : TZapMessage;
begin
  Queue := ZapMQ.Core.Context.Queues.Find(pQueueName);
  ZapMessage := Queue.GetMessage(pIdMessage);
  if ZapMessage.Status = zAnswered then
  begin
    Result := ZapMessage.Prepare.ToJSON.ToString;
    ZapMessage.Status := zSended;
  end
  else
    Result := string.Empty;
end;

function TZapMethods.UpdateMessage(const pQueueName : string; pMessage : string) : string;
var
  ZapMessage : TZapMessage;
  ZapJSONMessage : TZapJSONMessage;
begin
  ZapJSONMessage := TZapJSONMessage.FromJSON(pMessage);
  try
    if ZapJSONMessage.RPC then
    begin
      ZapMessage := TZapRPCMessage.Create;
      ZapMessage.QueueName := pQueueName;
      ZapMessage.TTL := ZapJSONMessage.TTL;
      ZapMessage.Body := TJSONObject.ParseJSONValue(
        TEncoding.ASCII.GetBytes(ZapJSONMessage.Body.ToString), 0) as TJSONObject;
      TZapRPCMessage(ZapMessage).Timeout := ZapJSONMessage.Timeout;
      Result := ZapMessage.Id;
    end
    else
    begin
      Result := string.Empty;
      ZapMessage := TZapMessage.Create;
      ZapMessage.QueueName := pQueueName;
      ZapMessage.TTL := ZapJSONMessage.TTL;
      ZapMessage.Body := TJSONObject.ParseJSONValue(
        TEncoding.ASCII.GetBytes(ZapJSONMessage.Body.ToString), 0) as TJSONObject;
    end;
    ZapMQ.Core.Context.Queues.AddMessage(ZapMessage);
  finally
    ZapJSONMessage.Free;
  end;
end;

function TZapMethods.UpdateRPCResponse(const pQueueName : string;
  const pIdMessage : string; pResponse : string): string;
var
  ZapMessage : TZapMessage;
  Queue : TZapQueue;
begin
  Queue := ZapMQ.Core.Context.Queues.Find(pQueueName);
  ZapMessage := Queue.GetMessage(pIdMessage);
  TZapRPCMessage(ZapMessage).Response := TJSONObject.ParseJSONValue(
    TEncoding.ASCII.GetBytes(pResponse), 0) as TJSONObject;
  ZapMessage.Status := zAnswered;
  Result := string.Empty;
end;

end.
