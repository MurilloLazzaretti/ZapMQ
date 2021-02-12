unit ZapMQ.Threads;

interface

uses
  System.classes, ZapMQ.Message, ZapMQ.Core, ZapMQ.Queue;

type
  TZapMQCleanerThread = class(TThread)
  private
    FStatusMessage : TZapMessageStatus;
    FContext : TZapCore;
  public
    procedure Execute; override;
    constructor Create(const pStatusMessage : TZapMessageStatus;
      const pContext : TZapCore); overload;
  end;

  TZapMQCheckExpirationThread = class(TThread)
  private
    FContext : TZapCore;
  public
    procedure Execute; override;
    constructor Create(const pContext : TZapCore); overload;
  end;

  TZapMQCheckSendedThread = class(TThread)
  private
    FContext : TZapCore;
  public
    procedure Execute; override;
    constructor Create(const pContext : TZapCore); overload;
  end;

implementation

{ TZapMQCleanerThread }

constructor TZapMQCleanerThread.Create(const pStatusMessage : TZapMessageStatus;
  const pContext : TZapCore);
begin
  inherited Create(True);
  FStatusMessage := pStatusMessage;
  FContext := pContext;
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
    Sleep(1000);
  end;
end;

{ TZapMQCheckExpirationThread }

constructor TZapMQCheckExpirationThread.Create(const pContext: TZapCore);
begin
  inherited Create(True);
  FContext := pContext;
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
    Sleep(1000);
  end;
end;

{ TZapMQCheckSendedThread }

constructor TZapMQCheckSendedThread.Create(const pContext: TZapCore);
begin
  inherited Create(True);
  FContext := pContext;
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
    Sleep(1000);
  end;
end;

end.
