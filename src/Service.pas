unit Service;

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Classes,
  Vcl.Graphics, Vcl.Controls, Vcl.SvcMgr, Vcl.Dialogs, ZapMQ.Threads;

type
  TZapMQservice = class(TService)
    procedure ServiceStart(Sender: TService; var Started: Boolean);
    procedure ServiceStop(Sender: TService; var Stopped: Boolean);
  private
    ExpiredCleanerMessages, ProcessedClearnerMessage : TZapMQCleanerThread;
    CheckExpirationMessages : TZapMQCheckExpirationThread;
    CheckSendedMessages : TZapMQCheckSendedThread;
  public
    function GetServiceController: TServiceController; override;
    { Public declarations }
  end;

var
  ZapMQservice: TZapMQservice;

implementation

uses
  ZapMQ.Core, ZapMQ.Message;

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
  TZapCore.Start;
  ExpiredCleanerMessages := TZapMQCleanerThread.Create(zExpired, ZapMQ.Core.Context);
  ExpiredCleanerMessages.Start;
  ProcessedClearnerMessage := TZapMQCleanerThread.Create(zProcessed, ZapMQ.Core.Context);
  ProcessedClearnerMessage.Start;
  CheckExpirationMessages := TZapMQCheckExpirationThread.Create(ZapMQ.Core.Context);
  CheckExpirationMessages.Start;
  CheckSendedMessages := TZapMQCheckSendedThread.Create(ZapMQ.Core.Context);
  CheckSendedMessages.Start;
  Started := True;
end;

procedure TZapMQservice.ServiceStop(Sender: TService; var Stopped: Boolean);
begin
  ExpiredCleanerMessages.Terminate;
  ProcessedClearnerMessage.Terminate;
  CheckExpirationMessages.Terminate;
  ExpiredCleanerMessages.Free;
  ProcessedClearnerMessage.Free;
  CheckExpirationMessages.Free;
  CheckSendedMessages.Free;
  TZapCore.Stop;
  Stopped := True;
end;

end.
