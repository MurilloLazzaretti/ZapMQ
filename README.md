## 🇧🇷  ZapMQ - Message Broker 🇧🇷

  <b>ZapMQ</b> is a simple and efficient message broker for Windows platform developed in Delphi. With him you can communicate yours micro services in a simple way usign already implemented Delphi Wrapper. New Wrapper is coming soon (.NET C#)

## ⚠️ Warning

ZapMQ is in a <b>Beta</b> version for now, if you have any issue, please tell us.

## ⚙️ Installation

Download and run ZapMQ - Installer.exe, specify the folder to install and next, next, next...
When installation is done, in your services list will be ZapMQ service installed and already running (if there was no problem, of course)

## ⚡️ Server Port

ZapMQ open a default HTTP port <b>5679</b>, if you want to change this port, go to application folder and edit ZapMQ.ini file. Dont forget to restart the service.

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

## 🕗 Performance

We are current in a beta version, so we dont have numbers about how many messages the ZapMQ can recive in a second or how many it can send in a second, but if you need a high performance like Kafka, RabbitMQ or others messages brokers this is not your place. ZapMQ was not developed to have an extremely high performance.

ZapMQ cant work in a cluster architecture too.

We think that ZapMQ can send/recive like 300 messages per second (or something more), but we dont put it in a tester yet. 

## 🌱 Wrappers

Implemented Wrappers for now :

| _Language_ | _Status_        | _Link_            | 
| ---------- | --------------- | ----------------- |
|  Delphi    | Done            | [`Delphi Wrapper`](https://github.com/MurilloLazzaretti/ZapMQ-Delphi-Wrapper)|
|  .NET C#   | Coming soon     | ❌                |

## 🔥 Uninstall

To uninstall the ZapMQ, under the folder of the installation, there is a file named "unins000.exe" just run it and next, next next...