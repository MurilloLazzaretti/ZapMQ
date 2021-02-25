unit ZapMQ.Queue;

interface

uses
  Generics.Collections,
  ZapMQ.Message;

type
  TZapQueue = class
  private
    FMessages : TObjectList<TZapMessage>;
    FName: string;
    procedure SetName(const Value: string);
  public
    property Name : string read FName write SetName;
    procedure AddMessage(const pMessage : TZapMessage);
    procedure RemoveMessage(const pMessage : TZapMessage);
    function GetMessage(const pIdMessage : string) : TZapMessage;
    procedure CleanMessages(const pStatusMessage : TZapMessageStatus);
    procedure CheckExpirationMessages;
    procedure CheckSendedMessages;
    procedure Clear;
    function GetNextMessageToProcess : TZapMessage;
    function Count : integer;
    constructor Create; overload;
    destructor Destroy; override;
  end;

  TZapQueues = class
  private
    FQueues : TObjectList<TZapQueue>;
    procedure NewQueue(const pQueueName : string; const pMessage : TZapMessage); overload;
  public
    function All : TObjectList<TZapQueue>;
    function Find(const pQueueName : string) : TZapQueue;
    procedure AddMessage(const pMessage : TZapMessage);
    constructor Create; overload;
    destructor Destroy; override;
  end;

implementation

uses
  System.SysUtils;

{ TZapQueue }

procedure TZapQueue.AddMessage(const pMessage: TZapMessage);
begin
  pMessage.Status := zPending;
  FMessages.Add(pMessage);
end;

procedure TZapQueue.CheckExpirationMessages;
var
  ZapMessage : TZapMessage;
begin
  for ZapMessage in FMessages do
    ZapMessage.CheckExpiration;
end;

procedure TZapQueue.CheckSendedMessages;
var
  ZapMessage : TZapMessage;
begin
  for ZapMessage in FMessages do
  begin
    if ZapMessage.Status = zSended then
    begin
      if not ZapMessage.RPC then
        ZapMessage.Status := zProcessed
      else
        ZapMessage.Status := zProcessing;
    end;
  end;
end;

procedure TZapQueue.CleanMessages(const pStatusMessage: TZapMessageStatus);
var
  ZapMessage : TZapMessage;
begin
  for ZapMessage in FMessages do
  begin
    if ZapMessage.Status = pStatusMessage then
      RemoveMessage(ZapMessage);
  end;
end;

procedure TZapQueue.Clear;
begin
  FMessages.Clear;
end;

function TZapQueue.Count: integer;
begin
  Result := FMessages.Count;
end;

constructor TZapQueue.Create;
begin
  FMessages := TObjectList<TZapMessage>.Create(True);
end;

destructor TZapQueue.Destroy;
begin
  FMessages.Free;
  inherited;
end;

function TZapQueue.GetMessage(const pIdMessage: string): TZapMessage;
var
  ZapMessage : TZapMessage;
begin
  Result := nil;
  for ZapMessage in FMessages do
  begin
    if ZapMessage.Id = pIdMessage then
    begin
      Result := ZapMessage;
      Break;
    end;
  end;
end;

function TZapQueue.GetNextMessageToProcess: TZapMessage;
var
  ZapMessage : TZapMessage;
begin
  Result := nil;
  for ZapMessage in FMessages do
  begin
    if ZapMessage.Status = zPending then
    begin
      Result := ZapMessage;
      Break;
    end;
  end;
end;

procedure TZapQueue.RemoveMessage(const pMessage: TZapMessage);
begin
  FMessages.Remove(pMessage);
end;

procedure TZapQueue.SetName(const Value: string);
begin
  FName := Value;
end;

{ TZapQueues }

procedure TZapQueues.AddMessage(const pMessage: TZapMessage);
var
  Queue : TZapQueue;
begin
  Queue := Find(pMessage.QueueName);
  if Assigned(Queue) then
    Queue.AddMessage(pMessage)
  else
    NewQueue(pMessage.QueueName, pMessage);
end;

function TZapQueues.All: TObjectList<TZapQueue>;
begin
  Result := FQueues;
end;

constructor TZapQueues.Create;
begin
  FQueues := TObjectList<TZapQueue>.Create(True);
end;

destructor TZapQueues.Destroy;
var
  Queue : TZapQueue;
begin
  for Queue in All do
  begin
    Queue.Clear;
  end;
  FQueues.Free;
  inherited;
end;

function TZapQueues.Find(const pQueueName: string): TZapQueue;
var
  Queue: TZapQueue;
begin
  Result := nil;
  for Queue in FQueues do
  begin
    if Queue.Name = pQueueName then
    begin
      Result := Queue;
      break;
    end;
  end;
end;

procedure TZapQueues.NewQueue(const pQueueName: string;
  const pMessage: TZapMessage);
var
  Queue : TZapQueue;
begin
  Queue := TZapQueue.Create;
  Queue.Name := pQueueName;
  Queue.AddMessage(pMessage);
  FQueues.Add(Queue);
end;

end.
