unit ZapMQ.Threads;

interface

uses
  System.classes, ZapMQ.Message, ZapMQ.Core, ZapMQ.Queue, SyncObjs;

type
  TZapMQCleanerThread = class(TThread)
  private
    FEvent : TEvent;
    FStatusMessage : TZapMessageStatus;
    FContext : TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pStatusMessage : TZapMessageStatus;
      const pContext : TZapCore); overload;
    destructor Destroy; override;
  end;

  TZapMQCheckExpirationThread = class(TThread)
  private
    FEvent : TEvent;
    FContext : TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pContext : TZapCore); overload;
    destructor Destroy; override;
  end;

  TZapMQCheckSendedThread = class(TThread)
  private
    FEvent : TEvent;
    FContext : TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pContext : TZapCore); overload;
    destructor Destroy; override;
  end;

  TZapMQQueueCleaner = class(TThread)
  private
    FEvent : TEvent;
    FContext : TZapCore;
  public
    procedure Execute; override;
    procedure Stop;
    constructor Create(const pContext : TZapCore); overload;
    destructor Destroy; override;
  end;

implementation

uses
  System.DateUtils, System.SysUtils;

{ TZapMQCleanerThread }

constructor TZapMQCleanerThread.Create(const pStatusMessage : TZapMessageStatus;
  const pContext : TZapCore);
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
  Queue : TZapQueue;
begin
  inherited;
  while not Terminated do
  begin
    for Queue in Context.Queues.All do
    begin
      Queue.CleanMessages(FStatusMessage);
    end;
    FEvent.WaitFor(1000);
  end;
end;

procedure TZapMQCleanerThread.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  while not Terminated do;
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
  Queue : TZapQueue;
begin
  inherited;
  while not Terminated do
  begin
    for Queue in Context.Queues.All do
    begin
      Queue.CheckExpirationMessages;
    end;
    FEvent.WaitFor(1000);
  end;
end;

procedure TZapMQCheckExpirationThread.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  while not Terminated do;
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
  Queue : TZapQueue;
begin
  inherited;
  while not Terminated do
  begin
    for Queue in Context.Queues.All do
    begin
      Queue.CheckSendedMessages;
    end;
    FEvent.WaitFor(1000);
  end;
end;

procedure TZapMQCheckSendedThread.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  while not Terminated do;
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
  Queue : TZapQueue;
begin
  inherited;
  while not Terminated do
  begin
    for Queue in Context.Queues.All do
    begin
      if Queue.Count = 0 then
      begin
        if IncMinute(Queue.LastRemovedMessage, 1) < Now then
        begin
          Context.Queues.RemoveQueue(Queue);
        end;
      end;
    end;
    FEvent.WaitFor(60000);
  end;
end;

procedure TZapMQQueueCleaner.Stop;
begin
  Terminate;
  FEvent.SetEvent;
  while not Terminated do;
end;

end.
