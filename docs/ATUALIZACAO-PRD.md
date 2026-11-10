# Atualização do ZapMQ em PRODUÇÃO (OJI-P127, 10.10.110.127)

Levantamento feito em 2026-11-04, **somente leitura** (nada foi alterado no servidor).
> **Este documento tem duas partes.** A **Parte 1** (seções 1 a 9) troca o 1.x pelo 2.x: ao fim, a mensageria funciona e os micro serviços são monitorados. A **Parte 2** (seção 10) configura o restante do painel (banco, serviços, tráfego, aplicação web, downloads, proxy e transporte) com as pastas **desta** produção, que são diferentes de DEV e QAS. A Parte 2 **não** entra na janela de 30 minutos.

Destino: **ZapMQ 2.19.1 + Worker Control 2.31.0** (`publish/ZapMQ-2.19.1-WorkerControl-2.31.0.zip`, 85 MB).

> **Atenção (10/11/2026): existe versão mais nova, testada em DEV e QAS: ZapMQ 2.20.0 + Worker Control 2.32.0** (`publish/ZapMQ-2.20.0-WorkerControl-2.32.0.zip`, SHA-256 `b8372b94f25ed0646902350ca4193ce1e06ac94c5bef527c23f18c2fbe3666ae`). Traz o transporte de arquivos de configuração (`env.json` etc., com pergunta de substituição) e a pergunta "continuar ou parar" quando um item falha. Os passos abaixo valem igual, trocando `2.19.1` por `2.20.0` e `2.31.0` por `2.32.0`. A pasta já extraída na PRD (2.19.1) precisa ser refeita com o zip novo (checklist item 1). Na instalação do QAS/DEV o Worker Control deixou `e_sqlite3.dll` preso uma vez (no DEV): se o instalador reclamar de arquivo em uso, confira com `Get-FileHash` se a DLL já é igual à nova e inicie o serviço.
Parada prevista: **10 a 15 minutos** de mensageria fora do ar (estimativa conservadora), dentro de uma janela de 30 minutos. **No QAS, a mesma troca levou menos de 3 minutos** (seção 5.1).

> Isto **não** é uma atualização 2.x → 2.x. A produção ainda roda o **1.x (Delphi)**; é a *troca do 1.x pelo 2.x*, o caminho "1.x instalado" do `instalar.ps1`. Guia geral: [INSTALACAO.md](INSTALACAO.md).

---

## 0. Pendências antes do dia da atualização do ZapMQ

Última verificação no servidor: **2026-11-09 10:46** (somente leitura). Só o que a **troca do 1.x pelo 2.x** exige; o resto da Parte 2 e o transporte ficam para depois.

| # | Pendência | Situação |
|---|---|---|
| 1 | Zip extraído em `C:\OjiTec\install\zapmq-2.19.1\` | **Feito e conferido.** `instalar.ps1` na raiz da pasta; `ZapMQ.exe` 2.19.1 e `WorkerControl.exe` 2.31.0 corretos |
| 2 | `Unblock-File` nos arquivos | **Feito e conferido.** 0 arquivos bloqueados |
| 3 | Avisar a monitoração (Zabbix e Elastic) de que o `ZapMQservice` vai parar | **Aberto** (não verificável pelo servidor) |
| 4 | Snapshot / ponto de restauração Veeam logo antes da janela e confirmar backup recente | **Aberto** (não verificável pelo servidor) |
| 5 | Avisar os usuários (RFID, e-mail, push, socket, automação) | **Aberto** |
| 6 | Definir data e horário da janela (30 min, baixo uso) | **Aberto** |
| 7 | Autorizar no dia o Claude a executar as etapas de escrita, com "vai" a cada etapa | **Aberto** (no dia) |

**Conferido por leitura, sem ação sua**
- Dependências de serviço: `ZapMQservice` e `WorkerControlService` sem `DEPENDENCIES`, e nenhum serviço depende do `ZapMQservice`.
- Serviços atuais: `ZapMQservice`, `WorkerControlService` e `nginx` rodando (1.x). 79 conexões na 5679.
- Disco: C: 18,5 GB livres (suficiente para os cerca de 100 MB dos executáveis novos e dos backups).
- `C:\Temp\bkp-zapmq-*` ainda não existe (é criado no dia, passo 4.4).

**Já feito antes do previsto: `OjiTecPDF.exe` movido**
- Está em `C:\OjiTec\micro-services\pdf\`, com o `OjiTec.ini`. Nada ficou na raiz de `micro-services\`.
- O `ConfigWorkers.json` foi alterado às 10:35 e aponta para `micro-services\pdf\OjiTecPDF.exe`.
- Os 3 processos do PDF reiniciaram às 10:35:32 já nesse caminho. O instalador copia esse `ConfigWorkers.json`, então o caminho novo segue para o 2.x.
- Conferir: nenhuma outra aplicação lia o `micro-services\OjiTec.ini` antigo da raiz (não achei referência no `ConfigWorkers.json`, mas não posso ver tudo que lê um arquivo).

**Atenção: memória.** RAM livre: **459 MB de 8 GB** (era 964 MB em 04/11). Os maiores são o Defender (`MsMpEng` 273 MB e `MsSense` 252 MB) e as APIs do IIS (`Ojitech.Api.*` de 150 a 165 MB cada). O ZapMQ 2 e o Worker Control 2 são mais pesados que os 1.x. Acompanhe a memória na janela. Se possível, peça à infra mais RAM ou um horário de menor uso.

**Ficam para depois da atualização:** renomear os 6 grupos (só antes do primeiro transporte QAS → PRD), usuário de banco do DBA, regras e aprovação do transporte, APKs, log JSON do NGINX e o resto da Parte 2.

**No NGINX, no dia, entra só** a publicação do painel em `/zapmq/` (seção 7).

## 1. Como a produção está hoje

| Item | Encontrado |
|---|---|
| Servidor | OJI-P127, Windows Server 2019 Standard, no ar desde 21/10/2026, relógio sincronizado com DC02 |
| ZapMQ | serviço `ZapMQservice` (Auto, Local System), `C:\Program Files (x86)\ZapMQ\ZapMQ.exe` (10/07/2025), porta **5679** (`ZapMQ.ini`). Na pasta há também `ZapMQNew1.exe` e `ZapMQOld.exe` |
| Worker Control | serviço `WorkerControlService`, `C:\Program Files (x86)\WorkerControl\WorkerControl.exe` (**2021**), com `ManagementStudio.exe` (fora de execução) |
| `ConfigWorkers.json` | 13 grupos de micro serviços em `C:\OjiTec\micro-services\...`, 30 processos. Só tem `ZapMQHost`, `ZapMQPort`, `RateLoadConfig` e `WorkerGroups`: nada a migrar para o `Environment.json` além do que o 2.x já faz sozinho. Alterado por último em 13/10/2026 |
| Quem usa a fila | 78 conexões na 5679, **todas de `::1`** (só esta máquina). Nenhum outro servidor depende da 5679 |
| Proxy | NGINX 1.26.2 (`C:\nginx`), rodando por **nssm** (serviço `nginx`), site `ojitech.ojipapeis.com.br` em 80 (redireciona) e 443. Serve `C:/OjiTec/www/webApplication`. **Tem `location ~* \.(html\|css\|js\|json)$`**: o `location` do painel precisa de `^~` |
| IIS | ~30 sites de API (`Ojitech Api ... 1/2`, PIAF, SSO); a porta 9002 a 9029 é o upstream do NGINX. Não são tocados |
| Banco | sem SQL Server local |
| Recursos | RAM **964 MB livres de 8 GB**; disco **C: 19 GB livres**, E: 35 GB, D: 0 |
| Defender | proteção em tempo real ligada; exclusões incluem `C:\OjiTec`, `C:\nginx` e `C:\Program Files (x86)\WorkerControl`. **`C:\Program Files (x86)\ZapMQ` não está excluída** |
| Monitoração | Zabbix Agent, Elastic Agent e Veeam instalados |

## 2. Decisões do plano

1. **Pasta de instalação: `C:\OjiTec`** (`-Pasta C:\OjiTec`), que cria `C:\OjiTec\ZapMQ` e `C:\OjiTec\WorkerControl`. Já está excluída do Defender, o que evita o bloqueio do executável novo ("Acesso negado" ao iniciar). O D: está vazio e não serve.
2. **O 1.x fica no lugar, parado.** O ZapMQ 1.x vira início Manual e o `ZapMQ.exe` antigo continua intacto. O Worker Control mantém o serviço, que apenas passa a apontar para o executável novo.
3. **Nunca rodar `unins000.exe`** (nem do ZapMQ, nem do Worker Control). Apagaria o serviço do 2.x.
4. **Ambiente: `-Ambiente PRD`**.
5. **Ensaio:** o QAS (OJI-P122, 10.10.110.122) já fez exatamente esta troca em 15/10/2026 (1.x em `Program Files (x86)` para o 2.x em `D:\OjiTec`), em dois passos, e vem sendo atualizado desde então até o 2.19.1 / 2.31.0. O caminho está provado com a mesma estrutura de serviços da PRD.

## 3. Pontos de atenção (leia antes)

- **Mensagens em trânsito se perdem** na troca (o broker guarda tudo em memória; vale para 1.x também). Escolha um horário de baixa carga e avise quem usa RFID, e-mail, push, socket e automação.
- **O Worker Control 2 inicia cada micro serviço na pasta do próprio executável**; o 1.x iniciava em `C:\Program Files (x86)\WorkerControl`. Essa pasta só tem o `ConfigWorkers.json` e o `ManagementStudio.exe`, então é improvável que algum micro serviço dependa dela. Depois da troca, confira se os 13 grupos sobem sem erro de arquivo não encontrado.
- **RAM apertada (964 MB livres).** O ZapMQ 2 e o agente são executáveis autocontidos (.NET). Acompanhe a memória nos primeiros minutos e no dia seguinte.
- **Zabbix/Elastic podem alertar** que `ZapMQservice` parou (vai ficar parado de propósito). Avise quem monitora, ou ajuste para `ZapMQServer`.
- **Não verificado no levantamento:** se algum serviço do Windows declara dependência de `ZapMQservice` (confira com `sc.exe qc` no passo 5) e se há ponto de restauração/backup Veeam da VM recente. Confirme com quem administra o servidor.

## 4. Antes da janela (sem parada, de véspera ou de manhã)

1. **Copie o zip à mão** (sem depender da rede entre o Mac e o servidor) para `C:\OjiTec\install\` e extraia **tudo** em `C:\OjiTec\install\zapmq-2.19.1\`, como no QAS. O `instalar.ps1` e as pastas `ZapMQ` e `WorkerControl` devem ficar **direto** dentro dessa pasta (não numa subpasta com o nome do zip). Confira: `Test-Path C:\OjiTec\install\zapmq-2.19.1\instalar.ps1` deve dar `True`.
2. No servidor, PowerShell **como administrador**, na pasta `C:\OjiTec\install\zapmq-2.19.1`:

   ```powershell
   Get-ChildItem -Recurse | Unblock-File
   ```
3. **Simule** (não muda nada) e leia o que ele diz que vai fazer:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -Pasta C:\OjiTec -Ambiente PRD -Simular
   ```

   Deve dizer que acha o ZapMQ 1.x em `Program Files (x86)\ZapMQ`, porta 5679, e o Worker Control 1.x com o `ConfigWorkers.json`. Se disser "instalação nova", **pare**: ele não achou o 1.x.
4. **Backups** (cópias, nada é movido):

   ```powershell
   $d = "C:\Temp\bkp-zapmq-$(Get-Date -f yyyyMMdd)"; New-Item $d -ItemType Directory
   Copy-Item 'C:\Program Files (x86)\ZapMQ' "$d\ZapMQ" -Recurse
   Copy-Item 'C:\Program Files (x86)\WorkerControl' "$d\WorkerControl" -Recurse
   Copy-Item C:\nginx\conf\nginx.conf "$d\nginx.conf"
   sc.exe qc ZapMQservice          | Out-File "$d\sc-zapmq.txt"
   sc.exe qc WorkerControlService  | Out-File "$d\sc-wc.txt"
   ```
5. Dependências de serviço (precisa estar vazio em `DEPENDENCIES`):

   ```powershell
   sc.exe qc ZapMQservice; sc.exe qc WorkerControlService
   Get-Service | ? { $_.ServicesDependedOn.Name -contains 'ZapMQservice' }
   ```
6. Se a VM tiver snapshot/ponto de restauração Veeam, peça um antes da janela.
7. Avise os usuários e a monitoração (item 3).
8. Tenha à mão o trecho do NGINX do passo 7 (já pode ser preparado em arquivo).

## 5. Na janela: roteiro com tempos

| Min | O que fazer | Para quê |
|---|---|---|
| 0 | Aviso final; confira `Invoke-RestMethod http://localhost:5679/health` e `Get-Service ZapMQservice, WorkerControlService, nginx` | Linha de base |
| 2 | Rode o instalador de verdade (comando abaixo) | Troca ZapMQ e Worker Control |
| 3-6 | O ZapMQ 1.x para, o `ZapMQServer` sobe e o instalador confere `/health` | **Mensageria parada daqui** |
| 6-12 | O Worker Control 1.x para (derruba os micro serviços, até ~30 s por grupo), o serviço aponta para o executável novo e sobe com o mesmo `ConfigWorkers.json` | Micro serviços voltam |
| 12-16 | Verificação (seção 6) | Confirmar |
| 16-22 | NGINX: publicar o painel (seção 7), trocar a senha, **Proteger agora** | Painel |
| 22-30 | Observação e folga para reverter, se preciso | Margem |

### 5.1 Tempos medidos no QAS (logs do instalador)

| Operação | Início | Fim | Duração |
|---|---|---|---|
| 15/10, troca do ZapMQ 1.x → 2.13.1 (`-Somente ZapMQ`) | 10:10:11 | 10:10:16 | **5 s** |
| 15/10, troca do Worker Control 1.x → 2.27.1 (`-Somente WorkerControl`) | 10:12:48 | 10:13:33 | **45 s** |
| 30/10, atualização 2.18.0 → 2.19.1 e WC 2.30.0 → 2.31.0 (os dois de uma vez) | 10:32:18 | 10:33:00 | **42 s** |

Ou seja, a parada total foi de uns **2 a 3 minutos**, incluindo a conferência entre os dois passos. O QAS tem 13 grupos com 3 instâncias (39 processos) e a PRD tem 13 grupos com 30 processos, então não se espera mais tempo na PRD; os 10 a 15 minutos acima são margem. O QAS fez em **dois passos** (primeiro o ZapMQ, depois o Worker Control, ~2 min entre eles): repita isso na PRD.

Comando da troca (o instalador volta sozinho ao que estava se o serviço novo não responder, e grava o `instalar-<data>.log` ao lado dele):

```powershell
powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -Pasta C:\OjiTec -Ambiente PRD
```

**Recomendado: dois tempos, como no QAS.** `-Somente ZapMQ`, verificar o `/health`, depois `-Somente WorkerControl`:

```powershell
powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -Pasta C:\OjiTec -Ambiente PRD -Somente ZapMQ
powershell -ExecutionPolicy Bypass -File .\instalar.ps1 -Pasta C:\OjiTec -Ambiente PRD -Somente WorkerControl
```

## 6. Verificação (logo após o instalador)

```powershell
Invoke-RestMethod http://localhost:5679/health                      # ok
Get-Service ZapMQServer, ZapMQservice, WorkerControlService         # Running, Stopped(Manual), Running
Get-Content C:\OjiTec\ZapMQ\logs\zapmq-*.log -Tail 30               # sem ERR; clientes conectando
Get-Content C:\OjiTec\WorkerControl\logs\workercontrol-*.log -Tail 60  # ServiceStarted, ConfigApplied, WorkerUp x30
(Get-Process | ? Name -match 'Ojit' | group Name).Count             # os 13 grupos de volta
```

Teste funcional: abrir o OjiTech e fazer algo que passe pela fila (gerar PDF, enviar e-mail de teste, ler uma etiqueta). A contagem de processos deve voltar a 30 (2+2+2+3+2+3+3+2+2+2+2+2+3, conforme o `ConfigWorkers.json`).

## 7. Painel no NGINX

No bloco `server { listen 443 ssl; ... }` de `C:\nginx\conf\nginx.conf`, **antes** do `location ~*`:

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
    client_max_body_size 600m;
}
```

```powershell
C:\nginx\nginx.exe -t -p C:\nginx        # tem de dizer "syntax is ok"
C:\nginx\nginx.exe -s reload -p C:\nginx  # recarrega sem derrubar o site
```

Se o teste falhar, **não recarregue**: restaure o `nginx.conf` do backup. O recarregamento não interrompe o OjiTech.

Depois: abrir `https://ojitech.ojipapeis.com.br/zapmq/`, entrar com `admin` / `admin`, **trocar a senha na hora**, e em *Processos e serviços* clicar em **Proteger agora**. O aviso "administração sem proteção" deve sumir. A porta 5680 só atende `localhost`.

## 8. Plano de volta

Decida o limite antes: **se aos 15 minutos a fila ou os micro serviços não estiverem de pé, volte.**

O instalador já volta sozinho se o serviço novo não responder. Para voltar à mão:

```powershell
# ZapMQ
Stop-Service ZapMQServer -Force
Set-Service ZapMQservice -StartupType Automatic
Start-Service ZapMQservice

# Worker Control (aponta de novo para o 1.x)
Stop-Service WorkerControlService -Force
sc.exe config WorkerControlService binPath= '"C:\Program Files (x86)\WorkerControl\WorkerControl.exe"'
Start-Service WorkerControlService
```

Para o NGINX, restaure o `nginx.conf` do backup e recarregue. Em último caso, restaure o ponto de restauração da VM.

## 9. Depois

- **No mesmo dia:** acompanhar o log de `ERR`, a RAM e o painel *Mensageria* (filas com consumidores).
- **Alguns dias depois, estável:** o `ZapMQservice` 1.x pode ser removido (`sc.exe delete ZapMQservice`) e a pasta antiga arquivada. **Nunca com o `unins000.exe`.**
- Ajustar *Configurações* no painel só quando for usar (banco, tráfego, frontends, transporte). Nada disso é obrigatório para a troca.
- Atualizações seguintes (2.x → 2.x) são o mesmo `instalar.ps1`, ou a seção 9 do [INSTALACAO.md](INSTALACAO.md).

---

# Parte 2: configurar o painel com as pastas da PRD

Fora da janela de 30 minutos, depois que a Parte 1 estiver estável. **Nada aqui é obrigatório para a mensageria funcionar**, e cada item é feito e verificado no painel (*Configurações*), que confere antes de gravar. As pastas abaixo foram lidas no servidor em 2026-11-05. **Não comparei com DEV e QAS** (não tenho acesso a eles): veja a seção 10.9.

## 10.1 Como a PRD é montada

| Peça | Na PRD |
|---|---|
| Micro serviços (Worker Control) | `C:\OjiTec\micro-services\<nome>\`, 13 grupos. `OjiTecPDF.exe` e `OjiTec.ini` ficam **soltos na raiz** `micro-services\`, não numa pasta própria |
| APIs | `C:\OjiTec\www\api\<Nome>\` (16 pastas: Cadastro, Formulacoes, Global, Logistica, Manutencao, PesquisaDesenvolvimento, Planejamento, Producao, Qualidade, Relatorio, RH, Seguranca, TI, DocumentacaoOnline, PIAF, SSO). No IIS são **dois sites por API** (`Ojitech Api Cadastro 1` e `2`, portas 9024/9025), **os dois apontando para a mesma pasta** |
| Entrada das APIs | NGINX, no bloco `listen 444 ssl`: `/api/<nome>` vai para o upstream de 2 portas (9002 a 9029). `/` cai em `global`. `/socketio/` vai para o Node na 3000 |
| Aplicação web | **Monolito** Angular (arquivos com hash, sem manifesto de micro frontends), em `C:\OjiTec\www\webApplication`, servido pelo NGINX direto do disco no bloco `listen 443 ssl`. Há `backupWebApplication` ao lado |
| Serviços do Windows (fora do Worker Control) | `frmOjiTec` (`C:\OjiTec\services\OjiTec.exe`), `OjitechTrancel` (`services\trancel`), `OjitechDriverTrancel1` (desabilitado), `Ojitech-Websocket-Server` (**nssm** + `node.exe`, script em `services\web-socket-server\index.js`), `nginx` (**nssm**) |
| nssm | `C:\OjiTec\setup\nssm\win64\nssm.exe` |
| Arquivos para download | `C:\OjiTec\www\APKs` (4 APKs + `web.config`), hoje no IIS (`Download APKs`, porta 450). Há também `www\padraoOperacional` (PDFs) |
| Banco | **remoto**: SQL Server `10.10.110.70\SIAPP`, base `SIAPP_PRODUCAO`. Não há SQL local |
| Logs das aplicações | `C:\OjiTec\log\<App>` (ApiCadastro, ApiGlobal, ApiQualidade, API PIAF, OjiTec, OjitechSocketIO, \_service) |
| Log do NGINX | `C:\nginx\logs\access.log` no formato **padrão** (sem tempo de resposta); **não há** `log_format`/`access_log` no `nginx.conf` |
| NGINX | `C:\nginx\nginx.exe`, config `C:\nginx\conf\nginx.conf`, serviço `nginx` (via nssm) |

## 10.2 Banco de dados (Configurações → Banco de dados)

- `Server`: `10.10.110.70\SIAPP`; `Databases`: `SIAPP_PRODUCAO`; `Name`: `PRD`.
- **Não reutilize o usuário das APIs** (o do `appsettings.json`). Peça ao DBA um usuário só de leitura de metadados: `VIEW SERVER STATE`, `VIEW DEFINITION` nas bases acompanhadas e leitura do `msdb` (backups e jobs). Nenhum dado de tabela é lido.
- Ajustes que valem rever para a PRD: `BackupHours` (0 = não olha backup; combine a janela de backup com o DBA), `DiskFreePercent`.
- Aplicar scripts de transporte exige permissão de criar/alterar objetos. Para a PRD, recomendo **não** dar essa permissão até decidir o fluxo de transporte (10.7).
- Teste a conexão do **servidor OJI-P127** para `10.10.110.70` (porta do SQL) antes. O agente roda como Local System e usa o usuário informado, não o do Windows.

## 10.3 Serviços do Windows (Processos e serviços → adicionar serviço)

- `Services.SuggestFrom`: `C:\OjiTec\services` (os sugeridos aparecem primeiro).
- Marcar: `frmOjiTec`, `OjitechTrancel`, `Ojitech-Websocket-Server`. `OjitechDriverTrancel1` está desabilitado: só acompanhe se for voltar a usar.
- `nginx` pode ser acompanhado, mas o painel o reinicia por nssm só se o serviço estiver cadastrado: **deixe `AutoRestart` desligado** em todos no começo (o padrão é manual).
- Logs a seguir pelo painel, se quiser: `C:\OjiTec\log\<App>\*.LOG` (formato de nome `dd-mm-aa.LOG`).

## 10.4 Tráfego HTTP (exige mexer no NGINX)

O log atual não serve (sem tempo de resposta). Acrescente no bloco `http { }`, **fora dos `server`**, para valer nos três (80/443/444):

```nginx
log_format zapmq escape=json
    '{"t":"$time_iso8601","ms":$msec,"ip":"$remote_addr","h":"$host",'
    '"m":"$request_method","u":"$request_uri","s":$status,"b":$body_bytes_sent,'
    '"rt":$request_time,"ua":"$upstream_addr","us":"$upstream_status",'
    '"ut":"$upstream_response_time","ref":"$http_referer"}';
access_log logs/access.log combined;
access_log logs/access.zapmq.log zapmq;
```

Sem o `access_log ... combined`, o log atual deixaria de ser gravado no bloco; mantenha as duas linhas. Procedimento: fazer cópia do `nginx.conf`, `nginx -t`, `nginx -s reload` (não derruba ninguém).

No painel: `Traffic.AccessLog` = `C:\nginx\logs\access.zapmq.log`; `ReopenCommand` = `C:\nginx\nginx.exe -s reopen -p C:\nginx`; `Ignore` = `["/zapmq/"]`; `GroupBy` = `["api"]`. O primeiro trecho do caminho (`/api/cadastro`) já agrupa por API. Note que `/socketio/` gera muito volume (polling a cada poucos segundos): decida se entra em `Ignore` para não poluir os números.

**Cuidado:** o log hoje tem `3,4 MB` (`access.log`) e o de erro `17,5 MB`; o novo arquivo cresce por requisição. O agente rotaciona sozinho (`RotateAtMb`, `KeepFiles`); confirme o espaço do C: (19 GB livres).

## 10.5 Aplicação web e downloads

- **Aplicação web**: `Root` = `C:\OjiTec\www\webApplication`, **`"Mode": "monolith"`**, `BaseUrl` = `https://ojitech.ojipapeis.com.br` (ou `https://127.0.0.1` com `Host` = `ojitech.ojipapeis.com.br`, se a máquina não resolver o próprio nome). Não há manifesto nem `version.json`; o cartão mostrará só a data de publicação.
- Atenção: no IIS existe o site `OjiTec` (parado) apontando para a mesma pasta. O painel pode listá-lo; ignore.
- **Distribuição de arquivos**: pasta `C:\OjiTec\www\APKs`. Hoje quem serve é o **IIS** (porta 450, com `web.config`). A tela oferece servir a pasta pelo NGINX; **não use "Preparar a pasta e o NGINX"** sem combinar, porque mudaria o endereço de download dos aplicativos. Cadastre a pasta apenas para o painel acompanhar/versionar, e mantenha o IIS servindo.
- `www\padraoOperacional` (PDFs) não é aplicação nem download de aplicativo: fica fora do painel.

## 10.6 Proxy reverso (aba do proxy)

`Proxy.Config` = `C:\nginx\conf\nginx.conf`; `Proxy.Executable` = `C:\nginx\nginx.exe`; `Proxy.Service` = `nginx`. O painel valida (`nginx -t`) antes de gravar e só recarrega com *Recarregar*. O **reinício** do serviço também testa antes, mas derruba conexões: na PRD, use só *Recarregar*.

Esta configuração é lida e **gravada pelo painel**: na PRD, restrinja quem pode editá-la (somente o `master`).

## 10.7 Transporte e regras do ambiente

- `Transport.Environment` = `PRD` (o instalador já grava com `-Ambiente PRD`).
- **Regras do ambiente** (sugeridas no próprio [TRANSPORTE.md](TRANSPORTE.md)): PRD **sem criar pacotes**, versões **apenas via transporte**, **quem montou não aprova** e **janela de aplicação** (defina o horário com a equipe).
- **Rotas**: a PRD só **recebe**. Em *Origens autorizadas*, autorize o QAS. Os relógios dos servidores precisam estar a menos de 5 minutos entre si (a PRD está sincronizada com o `DC02`).
- **`Transport.Keep`** (o que é do ambiente e nunca viaja): nas APIs, `appsettings*.json` e `web.config` (têm a cadeia de conexão da PRD). Sem isso, um pacote do QAS sobrescreveria a conexão com `10.10.110.70\SIAPP`. Confirme os nomes nos micro serviços (`appsettings*.json`, `*.ini`).
- **Duas instâncias por API** (`... 1` e `... 2` na mesma pasta): confirme em QAS que o transporte para e inicia **os dois sites e os dois pools** ao trocar a API. É a primeira coisa a testar.
- **`OjiTecPDF.exe`** está solto na raiz de `micro-services\` junto do `OjiTec.ini`: um alvo cuja pasta é a raiz de todos os micro serviços é perigoso. Antes de usar transporte nele, veja como o painel o trata em QAS e use `Keep` para o `.ini`. Se houver dúvida, deixe-o fora do transporte.
- **Serviços nssm** (`Ojitech-Websocket-Server`, `nginx`): o pacote leva o caminho do nssm, e o destino o corrige. Aqui é `C:\OjiTec\setup\nssm\win64\nssm.exe`.

## 10.8 Ordem sugerida e esforço

| Passo | Quem | Esforço | Risco |
|---|---|---|---|
| Banco (10.2) | DBA + você | pedir o usuário | baixo (só leitura) |
| Serviços do Windows (10.3) | você | 10 min | baixo |
| NGINX: log JSON + painel (10.4 e Parte 1, seção 7) | você | 15 min | médio (um `reload`, com `nginx -t` antes e backup) |
| Aplicação web (10.5) | você | 10 min | baixo |
| Proxy no painel (10.6) | você | 5 min | baixo se só leitura |
| Regras e transporte (10.7) | você + gestão | decisão de processo | o mais delicado |

Faça um passo por vez e verifique cada um no painel antes do seguinte.

## 10.9 PRD × QAS: o que muda (levantado em 2026-11-05)

O QAS (OJI-P122) já tem o painel configurado e serve de modelo. O **`Environment.json` dele está transcrito na seção 10.10**, a adaptar à PRD.

| Item | QAS | PRD | O que fazer |
|---|---|---|---|
| Disco da aplicação | `D:\OjiTec` (D: com 44 GB livres) | `C:\OjiTec` (C: 19 GB livres; D: tem 0 GB) | Instalar em `C:\OjiTec`. Ajustar todos os caminhos |
| ZapMQ / Worker Control | `D:\OjiTec\ZapMQ`, `...\WorkerControl` | serão `C:\OjiTec\ZapMQ`, `...\WorkerControl` | — |
| Pasta do instalador | `D:\OjiTec\install\zapmq-<versão>\` (guarda o zip de cada versão, com o `.log`) | — | Seguir o mesmo hábito: `C:\OjiTec\install\zapmq-2.19.1\` |
| Pasta das APIs | `D:\OjiTec\www\API\` | `C:\OjiTec\www\api\` | Ajustar `CreateFolders.api` |
| Aplicação web | `www\Angular`, host `qas.ojitech.ojipapeis.com.br`, HTTP | `www\webApplication`, host `ojitech.ojipapeis.com.br`, **HTTPS** (80 só redireciona) | `BaseUrl` por HTTPS; testar o certificado (seção 10.5) |
| NGINX | `C:\nginx`, nssm em `D:\ojitec\install\nssm`; já com `log_format zapmq` e `/zapmq/` | `C:\nginx`, nssm em `C:\OjiTec\setup\nssm`; **sem** log JSON nem `/zapmq/` | Fazer as duas edições (10.4 e Parte 1, seção 7) |
| APKs | servidos pelo **NGINX** (`location ^~ /apks/`, `alias`) | servidos pelo **IIS** (`Download APKs`, porta 450) | Não mexer na PRD sem combinar (10.5) |
| Banco | `10.10.110.69\SIAPP`, base `DB_OJITECH_QAS`, usuário `SA_SIAPP_QA` | `10.10.110.70\SIAPP`, base `SIAPP_PRODUCAO` | Usuário próprio, só leitura (10.2) |
| Entrada das APIs | porta 80, `qasapi.ojitech...`, sites 9002 a 9029 | porta 444 SSL, `/api/<nome>` por upstream, sites 9002 a 9029 | Só afeta `Traffic` (10.4) |
| Regras do ambiente | `AllowCreate: true`; sem `policy.json` | — | Definir para a PRD (10.7) |

### Diferenças que quebram o transporte QAS → PRD

O pacote leva **nomes lógicos**; cada ambiente os traduz. Se o nome não existir no destino, o item vira "alvo que ainda não existe" (TRANSPORTE.md, 4.1). Hoje os nomes **não casam**:

| Micro serviço (grupo) | QAS | PRD |
|---|---|---|
| Log | `Logs` | `Log` |
| PDF | `Formularios PDF` | `PDF` |
| Push | `Push Notifications` | `Push Notification` |
| RFID | `Antenas RFID` | `RFID` |
| Socket.IO | `SocketIO` | `Socket IO` |
| Office to PDF | `Office To PDF` | `officeToPDF` |
| Websocket (serviço Windows) | `Ojitech-WebSocket-Server` | `Ojitech-Websocket-Server` (maiúsculas diferentes) |
| Sites do IIS (API de documentação) | `Ojitech Api DocumentacaoOnline 1` | `Ojitech Api Documentacao Online 1` (com espaços) |
| SSO / PIAF | `Ojitech Api SSO`, `Ojitech Api PIAF` | `OjiTecApiSSO`, `OjiTecApiPIAF` |

Os outros sete grupos (Alertas, Email, RotuloEtiqueta, RotuloEtiquetaTrancel, Open XML, Automacao, Data Ware) têm o mesmo nome.

**Como alinhar (decidido):** depois da subida do 2.x e **antes de qualquer transporte QAS → PRD**, renomeie na PRD, pelo painel (*Processos e serviços → Configurar* → grupo → campo **Nome** → gravar). O QAS é a referência; a PRD passa para os nomes dele. Ao renomear, o grupo é recarregado: faça em horário de baixo uso, **um grupo por vez**, e confira em *Acompanhar* que os workers voltaram. O histórico de eventos anterior continua com o nome antigo. Os nomes de sites do IIS e o serviço do websocket (maiúsculas) não afetam a execução; confira na tela de transporte se a PRD os reconhece como o mesmo alvo e, se não, renomeie o serviço/site também.

**`OjiTecPDF.exe` na PRD está solto em `micro-services\`**; no QAS está em `micro-services\pdf\`. O alvo de um micro serviço é a **pasta do executável**: um pacote para o grupo `Formularios PDF` na PRD trocaria a raiz de todos os micro serviços. Mova-o para `micro-services\pdf\` (com o `OjiTec.ini` que ele usa, se for dele), ajuste o caminho no `ConfigWorkers.json` e teste. Faça em horário combinado, fora da janela da Parte 1.

Também diferem as contagens de instâncias (QAS: 3 por grupo; PRD: 2 ou 3): isso é do ambiente e deve ficar como está na PRD.

## 10.10 `Environment.json` de referência (copiado do QAS, adaptado à PRD)

Valores com **«…»** dependem de decisão ou do DBA. O painel valida ao gravar; prefira entrar pelas telas a editar o arquivo.

```json
{
  "Transport": {
    "AllowCreate": false,
    "StopSeconds": 90,
    "KeepVersions": 3,
    "Keep": ["appsettings*.json", "web.config", "ConfigWorkers.json", "*.ini",
             "environment.js", "env.json", "*.db", "*.db-wal", "*.db-shm", "logs/"],
    "Directory": "transport",
    "CreateFolders": {
      "worker": "C:\\OjiTec\\micro-services",
      "api": "C:\\OjiTec\\www\\api",
      "service": "C:\\OjiTec\\services"
    },
    "SiteName": "Ojitech Api {name} {n}",
    "Inboxes": {
      "worker":   "C:\\OjiTec\\WorkerControl\\transport\\inbox\\worker",
      "service":  "C:\\OjiTec\\WorkerControl\\transport\\inbox\\service",
      "api":      "C:\\OjiTec\\WorkerControl\\transport\\inbox\\api",
      "frontend": "C:\\OjiTec\\WorkerControl\\transport\\inbox\\frontend"
    }
  },
  "Proxy": {
    "Config": "C:\\nginx\\conf\\nginx.conf",
    "Executable": "C:\\nginx\\nginx.exe",
    "Service": "nginx"
  },
  "Traffic": {
    "AccessLog": "C:\\nginx\\logs\\access.zapmq.log",
    "RotateAtMb": 100, "KeepFiles": 5, "RetentionDays": 30,
    "MaxRoutes": 2000, "KeepErrors": 500,
    "GroupBy": ["api", "mfe"],
    "ReopenCommand": "C:\\nginx\\nginx.exe -s reopen",
    "Ignore": ["/zapmq/"]
  },
  "Frontends": [
    { "Name": "OJITECH", "Root": "C:\\OjiTec\\www\\webApplication", "Mode": "monolith",
      "Active": true, "Address": "ojitech.ojipapeis.com.br",
      "BaseUrl": "https://127.0.0.1", "Host": "ojitech.ojipapeis.com.br" }
  ],
  "Downloads": [
    { "Name": "APKs", "Root": "C:\\OjiTec\\www\\APKs", "Files": "*.apk", "VersionsFile": "versions.json" }
  ],
  "Database": {
    "Name": "PRD", "Server": "10.10.110.70\\SIAPP", "Databases": ["SIAPP_PRODUCAO"],
    "User": "«usuário só de leitura, do DBA»", "Password": "«…»",
    "SampleSeconds": 30, "RetentionDays": 7, "BlockingSeconds": 30,
    "BackupHours": 0, "DiskFreePercent": 10,
    "ObjectScanMinutes": 10, "ObjectHistoryDays": 365,
    "Encrypt": false, "TrustServerCertificate": true
  }
}
```

E, no `ConfigWorkers.json`, a seção de serviços (no QAS, `SuggestFrom` está vazio):

```json
"Services": { "SuggestFrom": [], "Items": [
  { "Name": "frmOjiTec" }, { "Name": "Ojitech-WebSocket-Server" }, { "Name": "OjitechTrancel" } ] }
```

Pontos de atenção deste arquivo:

- **`AllowCreate: false`** é a sugestão do TRANSPORTE.md para a PRD (o QAS está em `true`). O restante das regras (quem aprova, janela) fica em `transport/policy.json`, que o QAS ainda não tem: defina com a equipe.
- **`Keep`**: use a lista do QAS. Ela protege `appsettings*.json` e `web.config` (conexão com o banco da PRD) e `ConfigWorkers.json`, `*.ini` e os bancos locais do agente.
- **`BaseUrl: https://127.0.0.1`** pode falhar na validação do certificado (ele é do nome `ojipapeis`, não do IP). Teste na tela; se falhar, use `https://ojitech.ojipapeis.com.br` (o servidor resolve o próprio nome?) ou HTTP na porta 80 com `Host` e confirme que o redirecionamento não atrapalha a leitura.
- **`Downloads`** só cadastra a pasta para o painel; a PRD segue servindo os APKs pelo IIS. O `versions.json` da pasta será criado pelo painel quando houver pacote com APK.
- **`Database.User`/`Password`**: no QAS o usuário é `SA_SIAPP_QA`, de nome sugestivo de privilégio alto. Na PRD não replique isso.

## 10.11 O que ainda não foi analisado

- O **conteúdo** das pastas dos micro serviços da PRD (só a existência) e se algum deles lê arquivo relativo à pasta de trabalho antiga do Worker Control. No QAS a troca passou sem problema aparente, mas ele não foi inspecionado por dentro.
- Dependências de serviço e backup da VM da PRD (Parte 1, seção 3).
- Se a PRD resolve o próprio nome `ojitech.ojipapeis.com.br` (afeta `BaseUrl`).
- O `policy.json` e as rotas de transporte: o QAS tem `routes.json`, mas não li o conteúdo (guarda chaves). A autorização do QAS como origem na PRD é feita no painel.
