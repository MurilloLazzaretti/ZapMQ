unit ZapMQ.Methods;

interface

uses
  System.Classes,
  System.JSON;

type
{$METHODINFO ON}
  TZapMethods = class(TComponent)
  public
    function GetMessage(const pQueueName: string): string;
    function GetRPCResponse(const pQueueName, pIdMessage: string): string;
    function UpdateMessage(const pQueueName, pMessage: string): string;
    function UpdateRPCResponse(const pQueueName, pIdMessage, pMessage: string): string;
  end;
{$METHODINFO OFF}

implementation

uses
  ZapMQ.Core, ZapMQ.Message, ZapMQ.Queue, ZapMQ.Message.JSON, System.SysUtils;

{ TZapMethods }

function TZapMethods.GetMessage(const pQueueName: string): string;
var
  Queue: TZapQueue;
  ZapMessage: TZapMessage;
  ZapJSONMessage: TZapJSONMessage;
  JSON: TJSONObject;
begin
  Result := '';
  Queue := Context.Queues.Find(pQueueName);
  if Assigned(Queue) then
  begin
    ZapMessage := Queue.GetNextMessageToProcess;
    if Assigned(ZapMessage) then
    begin
      ZapJSONMessage := ZapMessage.Prepare;
      try
        JSON := ZapJSONMessage.ToJSON;
        try
          Result := JSON.ToString;
        finally
          JSON.Free;
        end;

        if ZapMessage.RPC then
          ZapMessage.Status := zSended
        else
          ZapMessage.Status := zProcessed;
      finally
        ZapJSONMessage.Free;
      end;
    end;
  end;
end;

function TZapMethods.GetRPCResponse(const pQueueName, pIdMessage: string): string;
var
  Queue: TZapQueue;
  ZapMessage: TZapMessage;
  ZapJSONMessage: TZapJSONMessage;
  JSON: TJSONObject;
begin
  Result := '';
  Queue := Context.Queues.Find(pQueueName);
  if Assigned(Queue) then
  begin
    ZapMessage := Queue.GetMessage(pIdMessage);
    if Assigned(ZapMessage) and (ZapMessage.Status = zAnswered) then
    begin
      ZapJSONMessage := ZapMessage.Prepare;
      try
        JSON := ZapJSONMessage.ToJSON;
        try
          Result := JSON.ToString;
        finally
          JSON.Free;
        end;

        ZapMessage.Status := zProcessed;
      finally
        ZapJSONMessage.Free;
      end;
    end;
  end;
end;

function TZapMethods.UpdateMessage(const pQueueName, pMessage: string): string;
var
  ZapJSONMessage: TZapJSONMessage;
  ZapMessage: TZapMessage;
begin
  ZapJSONMessage := TZapJSONMessage.FromJSON(pMessage);
  try
    ZapMessage := TZapMessage.Create;
    ZapMessage.QueueName := pQueueName;
    ZapMessage.TTL := ZapJSONMessage.TTL;

    if Assigned(ZapJSONMessage.Body) then
      ZapMessage.Body := TJSONObject.ParseJSONValue(ZapJSONMessage.Body.ToJSON) as TJSONObject;

    ZapMessage.RPC := ZapJSONMessage.RPC;
    ZapMessage.Response := TJSONObject.Create;

    Context.Queues.AddMessage(ZapMessage);
    Result := ZapMessage.Id;
  finally
    ZapJSONMessage.Free;
  end;
end;

function TZapMethods.UpdateRPCResponse(const pQueueName, pIdMessage, pMessage: string): string;
var
  Queue: TZapQueue;
  ZapMessage: TZapMessage;
  ParsedJSON: TJSONObject;
begin
  Result := '';
  Queue := Context.Queues.Find(pQueueName);
  if Assigned(Queue) then
  begin
    ZapMessage := Queue.GetMessage(pIdMessage);
    if Assigned(ZapMessage) then
    begin
      ParsedJSON := TJSONObject.ParseJSONValue(TEncoding.UTF8.GetBytes(pMessage), 0) as TJSONObject;
      if Assigned(ParsedJSON) then
      begin
        ZapMessage.Response := ParsedJSON;
        ZapMessage.Status := zAnswered;
        Result := 'OK';
      end;
    end;
  end;
end;

end.

