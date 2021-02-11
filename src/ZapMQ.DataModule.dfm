object ZapDataModule: TZapDataModule
  OldCreateOrder = False
  Height = 243
  Width = 216
  object Server: TDSServer
    AutoStart = False
    ChannelQueueSize = 1000
    Left = 64
    Top = 24
  end
  object Methods: TDSServerClass
    OnGetClass = MethodsGetClass
    Server = Server
    LifeCycle = 'Server'
    Left = 64
    Top = 88
  end
  object HTTPService: TDSHTTPService
    HttpPort = 5679
    DSPort = 5679
    Filters = <>
    Left = 64
    Top = 144
  end
end
