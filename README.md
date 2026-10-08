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

⚡ _Push and confirmation_ (protocol v2)

A client that speaks the v2 protocol keeps a connection open and gets each message the moment it arrives, then confirms it when done. Clients of both protocols can share a queue. The protocol is described in [`docs/PROTOCOLO-V2.md`](docs/PROTOCOLO-V2.md) (in Portuguese). The .NET wrapper uses it from version 2.0 on, and falls back to the old protocol by itself when the server is 1.x.

🪦 _Dead letters_

A message that expired, that nobody consumed, or that was delivered to a v2 client that went away before confirming it is kept as a dead letter, with the reason. Dead letters are not a queue: they are read, put back or discarded through the administration routes below. A message is never delivered a second time on the broker's own initiative.

🩺 _Health and metrics_

`GET /health` tells whether the service is up. `GET /metrics` lists the queues with their counters, the dead letters per queue and the connected v2 clients.

📄 _Log_

One log file per day, next to the executable. Warnings and errors also go to the Windows Event Log.

Messages and dead letters live in memory: restarting the service empties them. A message that nobody consumes leaves its queue after the retention time (180 seconds by default).

## 📋 Requirements

| Where | What |
| ----- | ---- |
| Build machine | [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the panel, [Node.js](https://nodejs.org) 22 or later. Any operating system |
| Server | Windows x64. Nothing to install: the executable carries the .NET runtime. Tested on Windows Server 2019 |

## 🔨 Build

From the repository root:

```
./publish.sh        # on Windows: .\publish.ps1
```

It compiles the panel interface (`src/ZapMQ.Panel`, Angular) and then the service with the interface inside. The folder `publish/win-x64` now has the two files the server needs:

| File | What it is |
| ---- | ---------- |
| `ZapMQ.exe` | the service, with the panel in it |
| `appsettings.json` | its settings |

`dotnet publish src/ZapMQ.Server -c Release -r win-x64 -o publish/win-x64` alone also works and needs no Node.js, but the service then has no panel interface, only its API.

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
    "LogLevel": "Information",
    "DeadLetters": {
      "MaxMessagesPerQueue": 1000,
      "MaxAgeHours": 168
    },
    "Queues": {
      "Orders": {
        "RetentionSeconds": 3600,
        "RedeliverUnconfirmed": false,
        "DeadLetters": { "MaxMessagesPerQueue": 5000 }
      }
    }
  }
}
```

Only `Port` is commonly changed. Everything else may be left out.

| Property | Default | Description |
| -------- | ------- | ----------- |
| `Port` | 5679 | HTTP port the service listens on |
| `RetentionSeconds` | 180 | Longest a message waits in a queue without being consumed |
| `EmptyQueueLifetimeSeconds` | 60 | How long a queue is kept after its last message leaves |
| `LogDirectory` | `logs` | Folder of the log files. Relative to the executable unless it is a full path |
| `LogRetentionDays` | 30 | How many daily log files are kept |
| `LogLevel` | `Information` | `Verbose`, `Debug`, `Information`, `Warning` or `Error` |
| `DeadLetters.MaxMessagesPerQueue` | 1000 | Dead letters kept per queue; the oldest leave first. `0` keeps none |
| `DeadLetters.MaxAgeHours` | 168 | Longest a dead letter is kept. `0` keeps none |
| `QueueDefinitionsFile` | `queues.json` | Where the queue settings made through the panel are kept. Relative to the executable unless it is a full path |
| `MessageModelsFile` | `models.json` | Where the message models saved through the panel are kept. Relative to the executable unless it is a full path |
| `Panel.Enabled` | true | `false` turns the panel and its port off |
| `Panel.Port` | 5680 | Port of the panel |
| `Panel.BasePath` | `/zapmq` | Path the panel is published under by a reverse proxy. It also answers at the root of its port |
| `Panel.User` | `admin` | The master the panel starts with, the first time it runs |
| `Panel.Password` | `admin` | Its password. Both are read only while there is no user yet; from then on the users are in their own file and the password is changed in the panel |
| `PanelUsersFile` | `users.json` | Where the users of the panel are kept, relative to the executable. No password is in it, only what tells whether one is right |
| `Transport.Environment` | name of the machine | How this environment is called in the packages it makes and takes in: `DEV`, `QAS`, `PRD` |
| `Transport.Directory` | `transport` | Where the packages are kept, relative to the executable |
| `Panel.SessionHours` | 8 | How long a login lasts |
| `V2.Enabled` | true | `false` turns the v2 protocol off: the service answers 1.x only and v2 wrappers fall back to it |
| `V2.MaxFrameBytes` | 4194304 | Largest v2 frame accepted |
| `V2.PingSeconds` | 15 | Interval of the keep-alive ping sent to v2 clients |
| `V2.PingTimeoutSeconds` | 30 | Silence after which a v2 client is considered gone |

`Queues` holds settings of individual queues, by name. A queue that is not listed uses the general values; queues do not have to be declared to exist.

| Property of a queue | Default | Description |
| ------------------- | ------- | ----------- |
| `RetentionSeconds` | the general one | Retention of this queue |
| `RedeliverUnconfirmed` | `false` | Puts a message that was delivered and not confirmed back in the queue instead of dead-lettering it. Only for queues where handling the same message twice does no harm |
| `KeepRecent` | none | How many of the last messages that went through the queue are kept, in memory, to be looked at in the panel with their content. Without it a message is only seen while somebody is watching the queue |
| `DeadLetters.MaxMessagesPerQueue` | the general one | Dead letters kept for this queue |
| `DeadLetters.MaxAgeHours` | the general one | Age limit of the dead letters of this queue |
| `Paused` | `false` | A paused queue keeps receiving and hands nothing to anybody |

The settings of a queue can also be made in the panel, with the service running. Those are written to `queues.json` and, at start, go on top of what `appsettings.json` says.

## 🖥 Panel

A web application served by the service itself, on a port of its own (5680), behind a login. It shows what is going through the broker and lets you act on it:

| Screen | What is there |
| ------ | ------------- |
| Overview | Messages per second, pending, in processing, dead letters and connected applications, with charts of the last hour and of the last day |
| Map | Everything that talks through the broker in one drawing: the reverse proxy and the applications it forwards to (when its traffic is measured), the applications that publish, the queues, the applications that consume and the Worker Control over what it keeps running. The lines show who publishes in and consumes from which queue and how much is going through; the colours, what is not as it should be (a queue with messages and nobody consuming, a queue piling up, dead letters, an application with processes missing, an unstable group). Queues that carry no work (the keep-alive, safe-stop and trace queues of each process) are left out |
| Queues | Every queue with its counters, live. For each one: its definition (retention, redelivery, dead-letter limits), editable and kept across restarts; the pending messages, to read; who publishes and who consumes; pause, resume and empty |
| Dead letters | What was not delivered or not confirmed, by queue and by reason: inspect, send back to the queue, discard |
| Applications | Who is connected over v2, by application and process, and the addresses still talking 1.x |
| Messages | Watch a queue: every message that goes through it, live, with who published it, who got it, what became of it and its content, without taking anything from the queue. Publish a message by hand, with a JSON editor, files and models saved by queue; a question (RPC) shows its answer |
| Trace | What one supervised process writes with `Trace()`, or all the processes of a group together when there is no telling which of them will take a message, live and from any machine: filter, pause, save to a file. The process only sends its trace while somebody is watching, and none of it is kept by the broker |
| Traffic | What goes through the reverse proxy, read from its access log by the Worker Control: requests, errors and answer times (median, 95th and 99th percentiles) in all and by application, endpoint and instance, compared with the day before, and the last server errors |
| Web application | The modules of a web application published as micro frontends: the version of each one, whether it answers, when it was published and what changed, with a timeline of publications |
| Processes and services | The Windows services of the machine that you choose to watch (state, uptime, processor, memory, an optional check of a port or address, start, stop and restart, and what happened to them), next to the groups and processes kept running by [Worker Control](https://github.com/MurilloLazzaretti/Worker-Control) 2.0, live: state, processor, memory and keep-alive answer time of each process, with charts; enable and disable a group, change its number of processes, restart a process or a group; the history of events; and `ConfigWorkers.json`, edited in forms or as text and validated before it takes effect |

The panel talks to the Worker Control through the broker (queue `WorkerControlAdmin`), so there is nothing to configure and the Worker Control may be on another machine. While it is not running, that screen says so and the rest of the panel works.

Open `http://<server>:5680/` and log in with the user and password of the settings (`admin` / `admin` until you change them).

Nothing of the panel answers on the messaging port, and nothing is loaded from the internet: interface, fonts and icons are inside the executable.

🔀 _Behind a reverse proxy_

The panel also answers under `Panel.BasePath`, so an existing site can publish it as a path of its own. For NGINX:

```nginx
location = /zapmq { return 301 /zapmq/; }

location ^~ /zapmq/ {
    proxy_pass http://127.0.0.1:5680/zapmq/;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header Connection "";
    # The panel keeps one request open to receive updates as they happen.
    proxy_buffering off;
    proxy_read_timeout 1h;
}
```

The `^~` matters when the site has locations by file extension (`location ~* \.js$` and the like), as a site serving a single-page application usually does: without it those would take the scripts and styles of the panel and look for them on disk.

## 🛠 Administration

The routes the panel is built on are on the panel port, under `/api`, and ask for the session of the login. The administration routes of earlier 2.x versions are there too, under `/admin`:

| Route | What it does |
| ----- | ------------ |
| `GET /admin/dead-letters` | Dead letters per queue, by reason |
| `GET /admin/dead-letters/{queue}` | The dead letters of a queue, newest first |
| `GET /admin/dead-letters/{queue}/{id}` | One dead letter |
| `POST /admin/dead-letters/{queue}/{id}/requeue` | Publishes a copy to the queue and removes the dead letter |
| `DELETE /admin/dead-letters/{queue}/{id}` | Discards one |
| `DELETE /admin/dead-letters/{queue}` | Discards all of a queue |
| `GET /admin/queues/{queue}` | The settings given to a queue |
| `PUT /admin/queues/{queue}` | Changes them, effective immediately, until the service restarts |
| `DELETE /admin/queues/{queue}` | Goes back to the general values |

To script against them, log in first (`POST /api/login` with `{"user": "...", "password": "..."}`) and send the cookie it returns. Message ids contain braces and have to be URL-encoded in the routes.

`GET /health` and `GET /metrics` stay on the messaging port, open, for whoever monitors the service.

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

The Delphi wrapper speaks the 1.x protocol and works with 2.x as it is. The .NET wrapper speaks both from its version 2.0 on and picks the one the server offers; its 1.x releases also keep working.

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
