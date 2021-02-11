unit ZapMQ.DataModule;

interface

uses
  System.SysUtils, System.Classes, Datasnap.DSCommonServer, Datasnap.DSServer,
  IPPeerServer, Datasnap.DSHTTP;

type
  TZapDataModule = class(TDataModule)
    Server: TDSServer;
    Methods: TDSServerClass;
    HTTPService: TDSHTTPService;
    procedure MethodsGetClass(DSServerClass: TDSServerClass;
      var PersistentClass: TPersistentClass);
  private
    { Private declarations }
  public
    { Public declarations }
  end;

var
  ZapDataModule: TZapDataModule;

implementation

uses
  ZapMQ.Methods;

{%CLASSGROUP 'System.Classes.TPersistent'}

{$R *.dfm}

procedure TZapDataModule.MethodsGetClass(DSServerClass: TDSServerClass;
  var PersistentClass: TPersistentClass);
begin
  PersistentClass := ZapMQ.Methods.TZapMethods;
end;

end.
