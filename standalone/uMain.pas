unit uMain;

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Variants,
  System.Classes, Vcl.Graphics, Vcl.Controls, Vcl.Forms, Vcl.Dialogs,
  ZapMQ.Threads, Vcl.StdCtrls;

type
  TForm1 = class(TForm)
    Button1: TButton;
    Button2: TButton;
    procedure Button1Click(Sender: TObject);
    procedure Button2Click(Sender: TObject);
  private
    ExpiredCleanerMessages, ProcessedClearnerMessage : TZapMQCleanerThread;
    CheckExpirationMessages : TZapMQCheckExpirationThread;
    CheckSendedMessages : TZapMQCheckSendedThread;
    ZapMQQueueCleaner : TZapMQQueueCleaner;
  public
    { Public declarations }
  end;

var
  Form1: TForm1;

implementation

uses
  ZapMQ.Core, ZapMQ.Message;

{$R *.dfm}

procedure TForm1.Button1Click(Sender: TObject);
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
  ZapMQQueueCleaner := TZapMQQueueCleaner.Create(ZapMQ.Core.Context);
  ZapMQQueueCleaner.Start;
end;

procedure TForm1.Button2Click(Sender: TObject);
begin
  ExpiredCleanerMessages.Stop;
  ProcessedClearnerMessage.Stop;
  CheckExpirationMessages.Stop;
  ZapMQQueueCleaner.Stop;
  ExpiredCleanerMessages.Free;
  ProcessedClearnerMessage.Free;
  CheckExpirationMessages.Free;
  CheckSendedMessages.Free;
  ZapMQQueueCleaner.Free;
  TZapCore.Stop;
end;

end.
