unit ZapMQ.Queue;

interface

uses
  System.Generics.Collections,
  System.SysUtils,
  System.SyncObjs,
  ZapMQ.Message;

type
  TZapQueue = class
  private
    FMessages: TObjectList<TZapMessage>;
    FLock: TCriticalSection;
    FName: string;
    FLastRemovedMessage: TDateTime;
    procedure SetName(const Value: string);
  public
    property Name: string read FName write SetName;
    property LastRemovedMessage: TDateTime read FLastRemovedMessage;

    constructor Create;
    destructor Destroy; override;

    procedure AddMessage(const pMessage: TZapMessage);
    procedure RemoveMessage(const pMessage: TZapMessage);
    function GetMessage(const pIdMessage: string): TZapMessage;
    function GetNextMessageToProcess: TZapMessage;
    function Count: Integer;

    procedure CleanMessages(const pStatusMessage: TZapMessageStatus); overload;
    procedure CleanMessages(const pStatusMessage: TZapMessageStatus; const pMaxAgeMSec: UInt64); overload;
    procedure CheckExpirationMessages;
    procedure CheckSendedMessages;
    procedure Clear;
  end;

  TZapQueues = class
  private
    FQueues: TObjectList<TZapQueue>;
    FLock: TCriticalSection;
    procedure NewQueue(const pQueueName: string; const pMessage: TZapMessage);
  public
    constructor Create;
    destructor Destroy; override;

    function All: TObjectList<TZapQueue>;
    function Find(const pQueueName: string): TZapQueue;
    procedure AddMessage(const pMessage: TZapMessage);
    procedure RemoveQueue(const pQueue: TZapQueue);
  end;

implementation

uses
  Winapi.Windows;

{ TZapQueue }

constructor TZapQueue.Create;
begin
  FMessages := TObjectList<TZapMessage>.Create(True);
  FLock := TCriticalSection.Create;
end;

destructor TZapQueue.Destroy;
begin
  FMessages.Free;
  FLock.Free;
  inherited;
end;

procedure TZapQueue.AddMessage(const pMessage: TZapMessage);
begin
  FLock.Enter;
  try
    pMessage.Status := zPending;
    FMessages.Add(pMessage);
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueue.RemoveMessage(const pMessage: TZapMessage);
begin
  FLock.Enter;
  try
    FMessages.Remove(pMessage);
    if FMessages.Count = 0 then
      FLastRemovedMessage := Now;
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueue.CheckExpirationMessages;
var
  Msg: TZapMessage;
begin
  FLock.Enter;
  try
    for Msg in FMessages do
      if Msg.Status = zPending then
        Msg.CheckExpiration;
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueue.CheckSendedMessages;
var
  Msg: TZapMessage;
begin
  FLock.Enter;
  try
    for Msg in FMessages do
    begin
      if Msg.Status = zSended then
      begin
        if not Msg.RPC then
          Msg.Status := zProcessed
        else
          Msg.Status := zProcessing;
      end;
    end;
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueue.CleanMessages(const pStatusMessage: TZapMessageStatus);
var
  I: Integer;
begin
  FLock.Enter;
  try
    for I := FMessages.Count - 1 downto 0 do
    begin
      if FMessages[I].Status = pStatusMessage then
        FMessages.Delete(I);
    end;
    if FMessages.Count = 0 then
      FLastRemovedMessage := Now;
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueue.CleanMessages(const pStatusMessage: TZapMessageStatus;
  const pMaxAgeMSec: UInt64);
var
  I: Integer;
  Msg: TZapMessage;
  CurrentTick: UInt64;
begin
  CurrentTick := GetTickCount64;
  FLock.Enter;
  try
    for I := FMessages.Count - 1 downto 0 do
    begin
      Msg := FMessages[I];
      if (Msg.Status = pStatusMessage) and ((CurrentTick - Msg.BirthTime) > pMaxAgeMSec) then
      begin
        FMessages.Delete(I);
      end;
    end;
    if FMessages.Count = 0 then
      FLastRemovedMessage := Now;
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueue.Clear;
begin
  FLock.Enter;
  try
    FMessages.Clear;
  finally
    FLock.Leave;
  end;
end;

function TZapQueue.Count: Integer;
begin
  FLock.Enter;
  try
    Result := FMessages.Count;
  finally
    FLock.Leave;
  end;
end;

function TZapQueue.GetMessage(const pIdMessage: string): TZapMessage;
var
  Msg: TZapMessage;
begin
  Result := nil;
  FLock.Enter;
  try
    for Msg in FMessages do
    begin
      if Msg.Id = pIdMessage then
        Exit(Msg);
    end;
  finally
    FLock.Leave;
  end;
end;

function TZapQueue.GetNextMessageToProcess: TZapMessage;
var
  Msg: TZapMessage;
begin
  Result := nil;
  FLock.Enter;
  try
    for Msg in FMessages do
    begin
      if Msg.Status = zPending then
        Exit(Msg);
    end;
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueue.SetName(const Value: string);
begin
  FName := Value;
end;

{ TZapQueues }

constructor TZapQueues.Create;
begin
  FQueues := TObjectList<TZapQueue>.Create(True);
  FLock := TCriticalSection.Create;
end;

destructor TZapQueues.Destroy;
begin
  FQueues.Free;
  FLock.Free;
  inherited;
end;

function TZapQueues.All: TObjectList<TZapQueue>;
begin
  Result := FQueues;
end;

function TZapQueues.Find(const pQueueName: string): TZapQueue;
var
  Queue: TZapQueue;
begin
  Result := nil;
  FLock.Enter;
  try
    for Queue in FQueues do
    begin
      if Queue.Name = pQueueName then
        Exit(Queue);
    end;
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueues.AddMessage(const pMessage: TZapMessage);
var
  Queue: TZapQueue;
begin
  FLock.Enter;
  try
    Queue := Find(pMessage.QueueName);
    if Assigned(Queue) then
      Queue.AddMessage(pMessage)
    else
      NewQueue(pMessage.QueueName, pMessage);
  finally
    FLock.Leave;
  end;
end;

procedure TZapQueues.NewQueue(const pQueueName: string; const pMessage: TZapMessage);
var
  Queue: TZapQueue;
begin
  Queue := TZapQueue.Create;
  Queue.Name := pQueueName;
  Queue.AddMessage(pMessage);
  FQueues.Add(Queue);
end;

procedure TZapQueues.RemoveQueue(const pQueue: TZapQueue);
begin
  FLock.Enter;
  try
    FQueues.Remove(pQueue);
  finally
    FLock.Leave;
  end;
end;

end.

