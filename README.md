## 🇧🇷  ZapMQ - Message Broker 🇧🇷
  <p>
    <a href="https://github.com/MurilloLazzaretti/ZapMQ/blob/main/img/ZapMQ.jpeg">
        <img alt="ZapMQ" height="150" src="https://github.com/MurilloLazzaretti/ZapMQ/blob/main/img/ZapMQ.jpeg">
    </a>  
  </p>
  <br>
  <b>ZapMQ</b> is a simple and efficient message broker for Windows platform developed in Delphi. With him you can communicate yours micro services in a simple way usign already implemented Delphi Wrapper. New Wrapper is coming soon (.NET C#)

## ⚠️ Warning

ZapMQ is in a <b>Beta</b> version for now, if you have any issue, please tell us.

## ⚙️ Installation

Download and run ZapMQ - Installer.exe, specify the folder to install and next, next, next...
When installation is done, in your services list will be ZapMQ service installed and already running (if there was no problem, of course)

## ⚡️ Server Port

ZapMQ open a default TCP port <b>5679</b>, if you want to change this port, go to application folder and edit ZapMQ.ini file. Dont forget to restart the service.

```iniFile
[ZapMQ]
Port=5679 
```
## 🧬 Resources

🚲 In any type of message, you will send/recive a JSON object.

Already implemented types of messages :

👂 _Publisher and Subscriber_

Send a message to a dermined queue with <b>no answer.</b> "One" of the "N" subscribers registred in this queue will process your message.  

🔌 _RPC_ 

Send a JSON object to a dermined queue with <b> answer.</b> "One" of the "N" subscribers registred in this queue will process your message and send an answer to the publisher. 

🌐 _Exchange_ (Coming soon)

Send a message to a dermined queue with <b>no answer.</b> "All" of the subscribers registred in this queue will process your message. 

## 🌱 Wrappers

Implemented Wrappers for now :

| _Language_ | _Status_        | _Link_            | 
| ---------- | --------------- | ----------------- |
|  Delphi    | Done            | [`Delphi Wrapper`](https://github.com/HashLoad/boss)|
|  .NET C#   | Coming soon     | ❌                |

## 🔥 Uninstall

To uninstall the ZapMQ, under the folder of the installation, there is a file named "unins000.exe" just run it and next, next next...