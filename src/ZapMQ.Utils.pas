unit ZapMQ.Utils;

interface

procedure LogError(const Msg: string);

implementation

uses
 Winapi.Windows, System.SysUtils;

procedure LogError(const Msg: string);
var
  hEventLog: THandle;
  pMsg: PChar;
begin
  hEventLog := RegisterEventSource(nil, PChar('ZapMQ'));
  if hEventLog <> 0 then
  begin
    try
      pMsg := PChar(Msg);
      ReportEvent(hEventLog, EVENTLOG_ERROR_TYPE, 0, 0, nil, 1, 0, @pMsg, nil);
    finally
      DeregisterEventSource(hEventLog);
    end;
  end;
end;

end.
