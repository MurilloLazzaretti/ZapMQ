object ZapDataModule: TZapDataModule
  OldCreateOrder = False
  Height = 272
  Width = 220
  object Server: TDSServer
    AutoStart = False
    ChannelQueueSize = 1000
    Left = 48
    Top = 24
  end
  object Methods: TDSServerClass
    OnGetClass = MethodsGetClass
    Server = Server
    LifeCycle = 'Server'
    Left = 48
    Top = 88
  end
  object HTTPService: TDSHTTPService
    HttpPort = 5679
    Server = Server
    DSPort = 5679
    Filters = <>
    Left = 48
    Top = 144
  end
end
