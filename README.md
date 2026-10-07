## 🇧🇷  ZapMQ - Message Broker 🇧🇷

<b>ZapMQ</b> is a simple message broker for Windows. Your services talk to each other through named queues, using the wrappers already available for .NET and Delphi.

## 🧭 Versions

| Version | Technology | Where |
| ------- | ---------- | ----- |
| 2.x     | .NET       | this branch (`main`) |
| 1.x     | Delphi     | branch [`delphi-v1`](https://github.com/MurilloLazzaretti/ZapMQ/tree/delphi-v1) |

Version 2.x is a rewrite. It speaks the same protocol as 1.x, so applications built with the existing wrappers keep working without being recompiled. The plan and its progress are in [`docs/PLANO-2.0.md`](docs/PLANO-2.0.md) (in Portuguese).

## 🧬 Resources

🚲 Every message carries a JSON object.

👂 _Publisher and Subscriber_

Send a message to a queue with <b>no answer</b>. Exactly one of the subscribers of that queue receives it.

🔌 _RPC_

Send a message to a queue and <b>get an answer</b>. Exactly one of the subscribers of that queue receives it and its answer goes back to the sender.

🩺 _Health and metrics_

`GET /health` tells whether the service is up. `GET /metrics` lists the queues with their counters (pending, delivered, answered, expired, dropped).

📄 _Log_

One log file per day, next to the executable. Warnings and errors also go to the Windows Event Log.

Messages live in memory: restarting the service empties the queues. A message that nobody consumes is discarded after the retention time (180 seconds by default).

## 📋 Requirements

| Where | What |
| ----- | ---- |
| Build machine | [.NET 10 SDK](https://dotnet.microsoft.com/download). Any operating system |
| Server | Windows x64. Nothing to install: the executable carries the .NET runtime. Tested on Windows Server 2019 |

## 🔨 Build

From the repository root:

```
dotnet publish src/ZapMQ.Server -c Release -r win-x64 -o publish/win-x64
```

The folder `publish/win-x64` now has the two files the server needs:

| File | What it is |
| ---- | ---------- |
| `ZapMQ.exe` | the service |
| `appsettings.json` | its settings |

## ⚡️ Configuration

Edit `appsettings.json`, in the same folder as `ZapMQ.exe`. Restart the service after changing it.

```json
{
  "ZapMQ": {
    "Port": 5679,
    "RetentionSeconds": 180,
    "EmptyQueueLifetimeSeconds": 60,
    "LogDirectory": "logs",
    "LogRetentionDays": 30,
    "LogLevel": "Information"
  }
}
```

| Property | Default | Description |
| -------- | ------- | ----------- |
| `Port` | 5679 | HTTP port the service listens on |
| `RetentionSeconds` | 180 | Longest a message stays in the broker, counted from when it was sent |
| `EmptyQueueLifetimeSeconds` | 60 | How long a queue is kept after its last message leaves |
| `LogDirectory` | `logs` | Folder of the log files. Relative to the executable unless it is a full path |
| `LogRetentionDays` | 30 | How many daily log files are kept |
| `LogLevel` | `Information` | `Verbose`, `Debug`, `Information`, `Warning` or `Error` |

## ⚙️ Installation

Run the commands in a PowerShell window opened as administrator. The examples use the folder `C:\ZapMQ`; a path without spaces keeps the commands simple.

1. Copy `ZapMQ.exe` and `appsettings.json` to `C:\ZapMQ`.

2. Adjust `appsettings.json` if you need a port other than 5679.

3. Register the service, make it restart by itself after a failure, and start it:

```powershell
sc.exe create ZapMQ binPath= "C:\ZapMQ\ZapMQ.exe" start= auto DisplayName= "ZapMQ"
sc.exe description ZapMQ "ZapMQ message broker"
sc.exe failure ZapMQ reset= 86400 actions= restart/5000/restart/5000/restart/60000
Start-Service ZapMQ
```

4. Open the port in the firewall if other machines will connect:

```powershell
New-NetFirewallRule -DisplayName "ZapMQ" -Direction Inbound -Protocol TCP -LocalPort 5679 -Action Allow
```

5. Check that it answers:

```powershell
Invoke-RestMethod http://localhost:5679/health
```

The answer is `status: ok`. The service runs under the Local System account and starts with Windows.

## 🔁 Coming from 1.x

Version 1.x registers its service as `ZapMQservice`, shown as "ZapMQ" in the services list. Both versions use port 5679, so only one of them can run at a time. Messages in transit at the moment of the switch are lost, as in any restart of the broker.

The file `ZapMQ.ini` is not read by 2.x. If you changed the port there, set the same port in `appsettings.json`.

_Replacing 1.x_

Stop and remove the old service, then follow the installation steps above:

```powershell
Stop-Service ZapMQservice -Force
& "C:\Program Files (x86)\ZapMQ\ZapMQ.exe" /uninstall
```

_Keeping 1.x installed, to be able to go back_

Windows does not accept two services with the same display name, so register 2.x under another name:

```powershell
sc.exe create ZapMQServer binPath= "C:\ZapMQ\ZapMQ.exe" start= auto DisplayName= "ZapMQ 2.0"
sc.exe failure ZapMQServer reset= 86400 actions= restart/5000/restart/5000/restart/60000

Stop-Service ZapMQservice -Force
Set-Service ZapMQservice -StartupType Manual
Start-Service ZapMQServer
```

To go back to 1.x:

```powershell
Stop-Service ZapMQServer -Force
Set-Service ZapMQservice -StartupType Automatic
Start-Service ZapMQservice
```

## ⬆️ Update

Replace the executable with the service stopped. `appsettings.json` stays as it is.

```powershell
Stop-Service ZapMQ -Force
Copy-Item .\ZapMQ.exe C:\ZapMQ\ZapMQ.exe -Force
Start-Service ZapMQ
```

## 🔥 Uninstall

```powershell
Stop-Service ZapMQ -Force
sc.exe delete ZapMQ
Remove-Item C:\ZapMQ -Recurse
```

## 🚑 Troubleshooting

| Symptom | Cause and what to do |
| ------- | -------------------- |
| The service does not start and Windows reports "Access is denied" for `ZapMQ.exe` | The antivirus is blocking an executable it does not know. Microsoft Defender does this when the rule that blocks executables by prevalence or age is on. Ask whoever administers the server to allow the file or its folder |
| The service stops right after starting | Another program is using the port, usually the 1.x service. See the newest file in the `logs` folder |
| It answers on the server but not from other machines | The firewall is not letting the port through. See step 4 of the installation |
| A client gets an error when sending or receiving | Refused calls are written to the log with the reason and the request that caused them |

## 🌱 Wrappers

| _Language_ | _Status_ | _Link_ |
| ---------- | -------- | ------ |
| Delphi     | Done     | [`Delphi Wrapper`](https://github.com/MurilloLazzaretti/ZapMQ-Delphi-Wrapper) |
| .NET C#    | Done     | [`.NET Wrapper C#`](https://github.com/MurilloLazzaretti/ZapMQ-.NET-Wrapper) |

Both wrappers were written for 1.x and work with 2.x as they are.

## 🔌 Compatibility with 1.x

Version 2.x answers the 1.x protocol the same way the Delphi server does. This is checked against answers recorded from a running 1.x server (`tests/contract`). Two things differ on purpose:

- A message with a TTL above 65535 milliseconds is accepted. In 1.x sending it failed.
- A message past its TTL is never delivered. In 1.x it could still be delivered for up to one second.

## 🧪 Development

```
dotnet test
```

The tests start a real server on a free local port and exercise it with plain HTTP requests and with the unmodified 1.x .NET wrapper.

To compare a running server with the recorded 1.x answers:

```
python3 tests/contract/contract.py record http://localhost:5679 out.json
python3 tests/contract/contract.py compare tests/contract/delphi-1.x.json out.json
```

## 🕗 Performance

ZapMQ was not built for very high throughput and does not run as a cluster. If you need what Kafka or RabbitMQ deliver, this is not the right tool.
