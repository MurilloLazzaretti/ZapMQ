# Instalação e migração: ZapMQ 2 e Worker Control 2

Como levar o ZapMQ 2 e o Worker Control 2 a um ambiente: instalação nova, ou troca de uma
instalação 1.x (Delphi) que está em uso. Vale para qualquer ambiente; o plano de cada um (nomes de
servidores, pastas, serviços) é feito a partir daqui, com o levantamento da seção 2.

## 1. O que é instalado

| Peça | Arquivos | Serviço do Windows | Portas |
|---|---|---|---|
| **ZapMQ** (broker e painel) | `ZapMQ.exe`, `appsettings.json` | `ZapMQServer` | fila: 5679; painel: 5680 (só local, atrás do proxy) |
| **Worker Control** (agente da máquina) | `WorkerControl.exe`, `e_sqlite3.dll`, `Microsoft.Data.SqlClient.SNI.dll`, `ConfigWorkers.json`, `Environment.json` | `WorkerControlService` | nenhuma: fala pelo ZapMQ |

- Os dois executáveis levam o .NET dentro: **não é preciso instalar runtime**.
- Os dois rodam como **Local System** e iniciam com o Windows.
- Os dados ficam ao lado de cada executável: logs, usuários do painel, pacotes de transporte,
  histórico e chaves.
- Os nomes `ZapMQServer` e `WorkerControlService` são os usados no restante deste documento.
  `WorkerControlService` é o **mesmo nome do 1.x**, de propósito: o Management Studio 1.x procura
  por ele.

## 2. Levantamento antes de começar

Anote, no ambiente de destino:

1. **O que está instalado hoje:** pasta, versão e nome do serviço do ZapMQ e do Worker Control
   (`sc.exe qc ZapMQservice`, `sc.exe qc WorkerControlService`), e se há o Management Studio.
2. **As portas:** a da fila no `ZapMQ.ini` do 1.x (padrão 5679) e se a 5680 está livre
   (`netstat -ano | findstr :5680`).
3. **Os micro serviços:** os grupos do `ConfigWorkers.json`, com a pasta e o executável de cada
   um, e com que wrapper falam (1.x ou 2.x). O ZapMQ 2 atende os dois protocolos: **nada nos
   micro serviços precisa mudar**.
4. **Os serviços do Windows** da aplicação que o painel vai acompanhar (nome, pasta).
5. **As APIs no IIS** (sites, pools, portas, pastas) e o **proxy reverso** (NGINX: pasta, arquivo
   de configuração, formato do log de acesso).
6. **A aplicação web** (pasta, se é monolito ou micro frontends) e as **pastas de arquivos para
   download**.
7. **O banco:** instância, bancos usados e o usuário que o agente vai usar (seção 8).
8. **Rede:** o nome do site no DNS interno e se os servidores dos outros ambientes o alcançam na
   porta 80/443 (para as rotas de transporte).
9. **Antivírus:** se há regra que bloqueia executáveis novos (o Microsoft Defender faz isso com a
   regra de prevalência). Combine a liberação da pasta com quem administra o servidor **antes**.

## 3. Com o instalador

O zip `ZapMQ-<versão>-WorkerControl-<versão>.zip` (montado por `installer/empacotar.sh`) faz as
seções 5, 6 e 9 sozinho: instalação nova, troca do 1.x ou atualização, decidindo pelo que encontra
na máquina, e volta ao que estava se o serviço novo não responder. Como usar está no `LEIA-ME.txt`
dentro dele; em resumo, como administrador:

```powershell
powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -Pasta D:\Apps -Ambiente QAS -Simular   # só mostra
powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -Pasta D:\Apps -Ambiente QAS
```

As decisões do script são testadas sem Windows por `installer/testar.ps1` (`pwsh ./installer/testar.ps1`).
As seções 5 e 6 abaixo são o mesmo caminho feito à mão.

## 4. Ordem

1. **ZapMQ 2** no lugar do ZapMQ 1.x. Os clientes 1.x continuam falando com ele.
2. **Worker Control 2** no lugar do Worker Control 1.x, com o mesmo `ConfigWorkers.json`.
3. **Painel** publicado pelo proxy reverso, troca da senha do `admin` e **proteção da fila de
   administração**.
4. O resto, conforme o ambiente: banco, tráfego, aplicação web, downloads, transporte.

Cada passo é verificado (seção 10) antes do seguinte. Os dois primeiros param a mensageria e os
micro serviços por alguns minutos: faça-os em horário combinado.

## 5. ZapMQ 2

1. Crie a pasta (por exemplo `D:\Apps\ZapMQ`) e copie `ZapMQ.exe` e `appsettings.json`.
2. Ajuste o `appsettings.json`:

   ```json
   {
     "ZapMQ": {
       "Port": 5679,
       "Panel": { "Port": 5680, "BasePath": "/zapmq" },
       "Transport": { "Environment": "QAS" }
     }
   }
   ```

   `Port` é a mesma do 1.x. `Transport.Environment` é o nome que o ambiente terá nos pacotes de
   transporte. O resto pode ficar no padrão; a lista completa está no README.

3. Registre o serviço novo **sem apagar o 1.x**, para poder voltar:

   ```powershell
   sc.exe create ZapMQServer binPath= "D:\Apps\ZapMQ\ZapMQ.exe" start= auto DisplayName= "ZapMQ 2.0"
   sc.exe failure ZapMQServer reset= 86400 actions= restart/5000/restart/5000/restart/60000
   ```

4. Troque:

   ```powershell
   Stop-Service ZapMQservice -Force
   Set-Service ZapMQservice -StartupType Manual
   Start-Service ZapMQServer
   ```

   As mensagens em trânsito no momento da troca se perdem, como em qualquer reinício do broker.

5. Verifique: `Invoke-RestMethod http://localhost:5679/health` responde `ok`, e o log em
   `logs\zapmq-*.log` mostra os clientes conectando.

**Voltar:** `Stop-Service ZapMQServer -Force; Set-Service ZapMQservice -StartupType Automatic; Start-Service ZapMQservice`.

## 6. Worker Control 2

> **Nunca use o desinstalador do Worker Control 1.x (`unins000.exe`).** O serviço tem o mesmo nome
> nas duas versões, e o desinstalador antigo para e apaga o serviço da 2.x junto. O 1.x sai
> trocando o executável do serviço (abaixo) e, depois de confirmado, apagando a pasta antiga.

1. Crie a pasta (por exemplo `D:\Apps\WorkerControl`) e copie `WorkerControl.exe`,
   `e_sqlite3.dll` e `Microsoft.Data.SqlClient.SNI.dll`.
2. Copie o `ConfigWorkers.json` do 1.x para ela. Ele é lido como está.
3. Aponte o serviço existente para o executável novo:

   ```powershell
   Stop-Service WorkerControlService -Force      # para os micro serviços também
   sc.exe config WorkerControlService binPath= "D:\Apps\WorkerControl\WorkerControl.exe"
   sc.exe failure WorkerControlService reset= 86400 actions= restart/5000/restart/5000/restart/60000
   Start-Service WorkerControlService
   ```

4. Verifique no log (`logs\workercontrol-*.log`) que os grupos subiram e cada processo respondeu
   (`WorkerUp`).

O que muda para os micro serviços: cada instância passa a iniciar **na pasta do próprio
executável** (o 1.x iniciava na pasta do serviço). Quem dependia disso precisa de
`WorkingDirectory` no grupo.

Na primeira leitura, as seções do ambiente (`Database`, `Transport`, `Traffic`, `Frontends`,
`Downloads`, `Proxy`) que estiverem no `ConfigWorkers.json` vão para o `Environment.json`, com
cópia `.bak` dos dois. O Management Studio 1.x continua funcionando, mas ao salvar ele descarta o
que não conhece: prefira o painel.

**Voltar:** pare o serviço e rode o `sc.exe config` de novo com o caminho do executável 1.x.

## 7. Painel

1. Publique pelo proxy reverso, no site que já existe (NGINX):

   ```nginx
   location = /zapmq { return 301 /zapmq/; }

   location ^~ /zapmq/ {
       proxy_pass http://127.0.0.1:5680/zapmq/;
       proxy_http_version 1.1;
       proxy_set_header Host $host;
       proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
       proxy_set_header Connection "";
       proxy_buffering off;
       proxy_read_timeout 1h;
       client_max_body_size 600m;   # upload de versões e pacotes de transporte
   }
   ```

   O `^~` é necessário quando o site tem `location` por extensão de arquivo (`\.js$` e afins).
   Teste e recarregue: `nginx -t` e `nginx -s reload`.

2. Entre com `admin` / `admin` e **troque a senha** (menu do usuário). Crie os demais usuários em
   *Usuários*.
3. Em *Processos e serviços*, clique em **Proteger agora**: a fila de administração do Worker
   Control passa a aceitar só pedidos selados com uma chave que só o ZapMQ e o agente têm. Sem
   isso, qualquer programa que alcance a porta da fila pode pedir ao agente o que o painel pede.

## 8. O resto, conforme o ambiente

Tudo em *Configurações*, no painel, que confere antes de gravar:

- **Banco de dados:** servidor, bancos acompanhados, usuário e senha (a senha é protegida pela
  máquina na primeira leitura). O usuário precisa ler as visões de gerenciamento
  (`VIEW SERVER STATE`), as definições dos objetos dos bancos acompanhados (`VIEW DEFINITION`) e o
  histórico de backups e jobs no `msdb`. Para o transporte **aplicar** scripts, também de criar e
  alterar objetos nesses bancos. Nenhum dado de tabela é lido.
- **Aplicações e transporte:** pastas onde ficam micro serviços, APIs e serviços; nome dos sites;
  o que é do ambiente e nunca vai num pacote (`Keep`).
- **Tráfego HTTP:** o NGINX passa a gravar um segundo log, em JSON (o formato padrão não tem tempo
  de resposta). O formato está em `docs/AMBIENTE.md`, seção 5.1.
- **Aplicação web** (monolito ou micro frontends) e **distribuição de arquivos** (downloads).
- **Rotas de transporte:** para enviar pacotes daqui, uma rota por ambiente seguinte; para
  recebê-los, autorize o ambiente anterior em *Origens autorizadas* (seção 5.3 de
  `docs/TRANSPORTE.md`). Os relógios dos servidores precisam estar a menos de 5 minutos uns dos
  outros.

## 9. Atualizações seguintes

Com o serviço parado, guarde o executável atual com a versão no nome, coloque o novo e inicie:

```powershell
Stop-Service ZapMQServer
Rename-Item D:\Apps\ZapMQ\ZapMQ.exe ZapMQ.2.11.0.exe
Copy-Item .\ZapMQ.exe D:\Apps\ZapMQ\
Start-Service ZapMQServer
```

O mesmo vale para o Worker Control. As configurações e os dados ficam como estão. Voltar é
renomear de volta.

## 10. Verificação

| O quê | Como |
|---|---|
| Broker | `http://localhost:5679/health` responde `ok` |
| Clientes | no painel, *Mensageria*: as filas de trabalho aparecem com consumidores |
| Micro serviços | *Processos e serviços*: todos os processos no ar, sem grupo instável |
| Proteção | o aviso "administração sem proteção" não aparece |
| Painel pelo proxy | `https://<site>/zapmq/` abre o login |
| Log do ZapMQ | sem `ERR` depois da partida |
| Log do agente | `ServiceStarted`, `ConfigApplied` e um `WorkerUp` por processo |
| Desligamento | reiniciar o servidor e conferir no log do agente `Windows is shutting down` seguido de `Stopped` |

## 11. Problemas conhecidos

| Sintoma | Causa |
|---|---|
| "Acesso negado" ao iniciar | O antivírus bloqueou o executável novo: libere a pasta |
| O ZapMQ para logo depois de iniciar | Outra coisa na porta, em geral o 1.x ainda de pé |
| O serviço do Worker Control sumiu | Alguém rodou o desinstalador do 1.x: recrie com `sc.exe create WorkerControlService binPath= "…\WorkerControl.exe" start= auto` |
| Um micro serviço demora 30 s para parar | Ele não atende o pedido de saída e é encerrado no fim do prazo do grupo |
| Botão Copiar não copia | Navegador antigo: com HTTP, a cópia usa a seleção do texto |
