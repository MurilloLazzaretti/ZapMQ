unit Service;

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Classes,
  Vcl.Graphics, Vcl.Controls, Vcl.SvcMgr, Vcl.Dialogs, ZapMQ.Threads,
  ZapMQ.Message;

type
  TZapMQservice = class(TService)
    procedure ServiceStart(Sender: TService; var Started: Boolean);
    procedure ServiceStop(Sender: TService; var Stopped: Boolean);
  private
    ExpiredCleanerMessages, ProcessedClearnerMessage,
      PendingClearnerMessage, SendedClearnerMessage, ProcessingClearnerMessage,
      AnsweredClearnerMessage: TZapMQCleanerThread;
    CheckExpirationMessages: TZapMQCheckExpirationThread;
    CheckSendedMessages: TZapMQCheckSendedThread;
    ZapMQQueueCleaner: TZapMQQueueCleaner;
  public
    function GetServiceController: TServiceController; override;
  end;

var
  ZapMQservice: TZapMQservice;

implementation

uses
  ZapMQ.Core;

{$R *.dfm}

procedure ServiceController(CtrlCode: DWord); stdcall;
begin
  ZapMQservice.Controller(CtrlCode);
end;

function TZapMQservice.GetServiceController: TServiceController;
begin
  Result := ServiceController;
end;

procedure TZapMQservice.ServiceStart(Sender: TService; var Started: Boolean);
begin
  if Assigned(ExpiredCleanerMessages) then
  begin
    ExpiredCleanerMessages.Stop;
    ExpiredCleanerMessages.WaitFor;
    ExpiredCleanerMessages.Free;
    ExpiredCleanerMessages := nil;
  end;

  if Assigned(ProcessedClearnerMessage) then
  begin
    ProcessedClearnerMessage.Stop;
    ProcessedClearnerMessage.WaitFor;
    ProcessedClearnerMessage.Free;
    ProcessedClearnerMessage := nil;
  end;

  if Assigned(CheckExpirationMessages) then
  begin
    CheckExpirationMessages.Stop;
    CheckExpirationMessages.WaitFor;
    CheckExpirationMessages.Free;
    CheckExpirationMessages := nil;
  end;

  if Assigned(CheckSendedMessages) then
  begin
    CheckSendedMessages.Stop;
    CheckSendedMessages.WaitFor;
    CheckSendedMessages.Free;
    CheckSendedMessages := nil;
  end;

  if Assigned(ZapMQQueueCleaner) then
  begin
    ZapMQQueueCleaner.Stop;
    ZapMQQueueCleaner.WaitFor;
    ZapMQQueueCleaner.Free;
    ZapMQQueueCleaner := nil;
  end;

  TZapCore.Start;

  ExpiredCleanerMessages := TZapMQCleanerThread.Create(zExpired, ZapMQ.Core.Context);
  ExpiredCleanerMessages.Start;

  ProcessedClearnerMessage := TZapMQCleanerThread.Create(zProcessed, ZapMQ.Core.Context);
  ProcessedClearnerMessage.Start;

  PendingClearnerMessage := TZapMQCleanerThread.Create(zPending, ZapMQ.Core.Context);
  PendingClearnerMessage.Start;

  SendedClearnerMessage := TZapMQCleanerThread.Create(zSended, ZapMQ.Core.Context);
  SendedClearnerMessage.Start;

  ProcessingClearnerMessage := TZapMQCleanerThread.Create(zProcessing, ZapMQ.Core.Context);
  ProcessingClearnerMessage.Start;

  AnsweredClearnerMessage := TZapMQCleanerThread.Create(zAnswered, ZapMQ.Core.Context);
  AnsweredClearnerMessage.Start;

  CheckExpirationMessages := TZapMQCheckExpirationThread.Create(ZapMQ.Core.Context);
  CheckExpirationMessages.Start;

  CheckSendedMessages := TZapMQCheckSendedThread.Create(ZapMQ.Core.Context);
  CheckSendedMessages.Start;

  ZapMQQueueCleaner := TZapMQQueueCleaner.Create(ZapMQ.Core.Context);
  ZapMQQueueCleaner.Start;

  Started := True;
end;

procedure TZapMQservice.ServiceStop(Sender: TService; var Stopped: Boolean);
begin
  if Assigned(ExpiredCleanerMessages) then
  begin
    ExpiredCleanerMessages.Stop;
    ExpiredCleanerMessages.WaitFor;
    ExpiredCleanerMessages.Free;
    ExpiredCleanerMessages := nil;
  end;

  if Assigned(ProcessedClearnerMessage) then
  begin
    ProcessedClearnerMessage.Stop;
    ProcessedClearnerMessage.WaitFor;
    ProcessedClearnerMessage.Free;
    ProcessedClearnerMessage := nil;
  end;

  if Assigned(PendingClearnerMessage) then
  begin
    PendingClearnerMessage.Stop;
    PendingClearnerMessage.WaitFor;
    PendingClearnerMessage.Free;
    PendingClearnerMessage := nil;
  end;

  if Assigned(SendedClearnerMessage) then
  begin
    SendedClearnerMessage.Stop;
    SendedClearnerMessage.WaitFor;
    SendedClearnerMessage.Free;
    SendedClearnerMessage := nil;
  end;

  if Assigned(ProcessingClearnerMessage) then
  begin
    ProcessingClearnerMessage.Stop;
    ProcessingClearnerMessage.WaitFor;
    ProcessingClearnerMessage.Free;
    ProcessingClearnerMessage := nil;
  end;

  if Assigned(AnsweredClearnerMessage) then
  begin
    AnsweredClearnerMessage.Stop;
    AnsweredClearnerMessage.WaitFor;
    AnsweredClearnerMessage.Free;
    AnsweredClearnerMessage := nil;
  end;

  if Assigned(CheckExpirationMessages) then
  begin
    CheckExpirationMessages.Stop;
    CheckExpirationMessages.WaitFor;
    CheckExpirationMessages.Free;
    CheckExpirationMessages := nil;
  end;

  if Assigned(CheckSendedMessages) then
  begin
    CheckSendedMessages.Stop;
    CheckSendedMessages.WaitFor;
    CheckSendedMessages.Free;
    CheckSendedMessages := nil;
  end;

  if Assigned(ZapMQQueueCleaner) then
  begin
    ZapMQQueueCleaner.Stop;
    ZapMQQueueCleaner.WaitFor;
    ZapMQQueueCleaner.Free;
    ZapMQQueueCleaner := nil;
  end;

  TZapCore.Stop;
  Stopped := True;
end;

end.

