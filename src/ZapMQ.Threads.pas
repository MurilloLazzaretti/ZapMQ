unit ZapMQ.Threads;

interface

uses
  System.Classes, ZapMQ.Message, ZapMQ.Core, ZapMQ.Queue, SyncObjs;

type
  TZapMQCleanerThread = class(TThread)
  private
    FEvent: TEvent;
    FStatusMessage: TZapMessageStatus;
    FContext: TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pStatusMessage: TZapMessageStatus; const pContext: TZapCore);
    destructor Destroy; override;
  end;

  TZapMQCheckExpirationThread = class(TThread)
  private
    FEvent: TEvent;
    FContext: TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pContext: TZapCore);
    destructor Destroy; override;
  end;

  TZapMQCheckSendedThread = class(TThread)
  private
    FEvent: TEvent;
    FContext: TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pContext: TZapCore);
    destructor Destroy; override;
  end;

  TZapMQQueueCleaner = class(TThread)
  private
    FEvent: TEvent;
    FContext: TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pContext: TZapCore);
    destructor Destroy; override;
  end;

implementation

uses
  System.SysUtils, System.DateUtils, ZapMQ.Utils;

{ TZapMQCleanerThread }

constructor TZapMQCleanerThread.Create(const pStatusMessage: TZapMessageStatus; const pContext: TZapCore);
begin
  inherited Create(True);
  FStatusMessage := pStatusMessage;
  FContext := pContext;
  FEvent := TEvent.Create(nil, True, False, '');
end;

destructor TZapMQCleanerThread.Destroy;
begin
  FEvent.Free;
  inherited;
end;

procedure TZapMQCleanerThread.Execute;
var
  Queue: TZapQueue;
begin
  inherited;
  while not Terminated do
  begin
    try
      FContext.QueueLock.Enter;
      try
        for Queue in FContext.Queues.All do
        begin
          if ((FStatusMessage = zProcessed) or (FStatusMessage = zExpired)) then
            Queue.CleanMessages(FStatusMessage)
          else
            Queue.CleanMessages(FStatusMessage, 180000);
        end;
      finally
        FContext.QueueLock.Leave;
      end;
    except
      on E: Exception do
        LogError('CleanerThread: ' + E.Message);
    end;

    FEvent.ResetEvent;
    FEvent.WaitFor(1000);
  end;
end;

procedure TZapMQCleanerThread.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  WaitFor;
end;

{ TZapMQCheckExpirationThread }

constructor TZapMQCheckExpirationThread.Create(const pContext: TZapCore);
begin
  inherited Create(True);
  FContext := pContext;
  FEvent := TEvent.Create(nil, True, False, '');
end;

destructor TZapMQCheckExpirationThread.Destroy;
begin
  FEvent.Free;
  inherited;
end;

procedure TZapMQCheckExpirationThread.Execute;
var
  Queue: TZapQueue;
begin
  inherited;
  while not Terminated do
  begin
    try
      FContext.QueueLock.Enter;
      try
        for Queue in FContext.Queues.All do
        begin
          Queue.CheckExpirationMessages;
        end;
      finally
        FContext.QueueLock.Leave;
      end;
    except
      on E: Exception do
        LogError('ExpirationThread: ' + E.Message);
    end;

    FEvent.ResetEvent;
    FEvent.WaitFor(1000);
  end;
end;

procedure TZapMQCheckExpirationThread.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  WaitFor;
end;

{ TZapMQCheckSendedThread }

constructor TZapMQCheckSendedThread.Create(const pContext: TZapCore);
begin
  inherited Create(True);
  FContext := pContext;
  FEvent := TEvent.Create(nil, True, False, '');
end;

destructor TZapMQCheckSendedThread.Destroy;
begin
  FEvent.Free;
  inherited;
end;

procedure TZapMQCheckSendedThread.Execute;
var
  Queue: TZapQueue;
begin
  inherited;
  while not Terminated do
  begin
    try
      FContext.QueueLock.Enter;
      try
        for Queue in FContext.Queues.All do
        begin
          Queue.CheckSendedMessages;
        end;
      finally
        FContext.QueueLock.Leave;
      end;
    except
      on E: Exception do
        LogError('SendedThread: ' + E.Message);
    end;

    FEvent.ResetEvent;
    FEvent.WaitFor(1000);
  end;
end;

procedure TZapMQCheckSendedThread.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  WaitFor;
end;

{ TZapMQQueueCleaner }

constructor TZapMQQueueCleaner.Create(const pContext: TZapCore);
begin
  inherited Create(True);
  FContext := pContext;
  FEvent := TEvent.Create(nil, True, False, '');
end;

destructor TZapMQQueueCleaner.Destroy;
begin
  FEvent.Free;
  inherited;
end;

procedure TZapMQQueueCleaner.Execute;
var
  Queue: TZapQueue;
  QueuesToRemove: TArray<TZapQueue>;
  I: Integer;
begin
  inherited;
  while not Terminated do
  begin
    try
      FContext.QueueLock.Enter;
      try
        SetLength(QueuesToRemove, 0);
        for Queue in FContext.Queues.All do
        begin
          if (Queue.Count = 0) and (Queue.LastRemovedMessage > 0) then
          begin
            if IncMinute(Queue.LastRemovedMessage, 1) < Now then
              QueuesToRemove := QueuesToRemove + [Queue];
          end;
        end;

        for I := Low(QueuesToRemove) to High(QueuesToRemove) do
        begin
          FContext.Queues.RemoveQueue(QueuesToRemove[I]);
        end;
      finally
        FContext.QueueLock.Leave;
      end;
    except
      on E: Exception do
        LogError('QueueCleanerThread: ' + E.Message);
    end;

    FEvent.ResetEvent;
    FEvent.WaitFor(60000);
  end;
end;

procedure TZapMQQueueCleaner.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  WaitFor;
end;

end.

