program ZapMQ;

uses
  Vcl.SvcMgr,
  ZapMQ.Core in 'src\ZapMQ.Core.pas',
  ZapMQ.DataModule in 'src\ZapMQ.DataModule.pas' {ZapDataModule: TDataModule},
  ZapMQ.Message.JSON in 'src\ZapMQ.Message.JSON.pas',
  ZapMQ.Message in 'src\ZapMQ.Message.pas',
  ZapMQ.Methods in 'src\ZapMQ.Methods.pas',
  ZapMQ.Queue in 'src\ZapMQ.Queue.pas',
  ZapMQ.Threads in 'src\ZapMQ.Threads.pas',
  Service in 'src\Service.pas' {ZapMQservice: TService};

{$R *.RES}

begin
  // Windows 2003 Server requires StartServiceCtrlDispatcher to be
  // called before CoRegisterClassObject, which can be called indirectly
  // by Application.Initialize. TServiceApplication.DelayInitialize allows
  // Application.Initialize to be called from TService.Main (after
  // StartServiceCtrlDispatcher has been called).
  //
  // Delayed initialization of the Application object may affect
  // events which then occur prior to initialization, such as
  // TService.OnCreate. It is only recommended if the ServiceApplication
  // registers a class object with OLE and is intended for use with
  // Windows 2003 Server.
  //
  // Application.DelayInitialize := True;
  //
  if not Application.DelayInitialize or Application.Installing then
    Application.Initialize;
  Application.CreateForm(TZapMQservice, ZapMQservice);
  Application.Run;
end.
