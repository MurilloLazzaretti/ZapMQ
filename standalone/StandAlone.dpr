program StandAlone;

uses
  Vcl.Forms,
  uMain in 'uMain.pas' {Form1},
  ZapMQ.Core in '..\src\ZapMQ.Core.pas',
  ZapMQ.Queue in '..\src\ZapMQ.Queue.pas',
  ZapMQ.Threads in '..\src\ZapMQ.Threads.pas',
  ZapMQ.DataModule in '..\src\ZapMQ.DataModule.pas' {ZapDataModule: TDataModule},
  ZapMQ.Message.JSON in '..\src\ZapMQ.Message.JSON.pas',
  ZapMQ.Message in '..\src\ZapMQ.Message.pas',
  ZapMQ.Methods in '..\src\ZapMQ.Methods.pas';

{$R *.res}

begin
  Application.Initialize;
  Application.MainFormOnTaskbar := True;
  Application.CreateForm(TForm1, Form1);
  Application.CreateForm(TZapDataModule, ZapDataModule);
  Application.Run;
end.
