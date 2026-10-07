# ZapMQ 2.0 e Worker Control 2.0 — Plano de migração para .NET

Situação: fases 0 a 3 concluídas; versão 2.0 instalada no ambiente de desenvolvimento (fase 4).
Última revisão: 2026-10-07.

Este documento é a referência para a reescrita do ZapMQ e do Worker Control em .NET. Ele registra as premissas, as decisões já tomadas, o escopo por versão e a ordem de execução. O desenho detalhado do protocolo v2 e das telas do painel será feito em documentos próprios, a partir daqui.

---

## 1. Objetivo

- Reescrever o servidor ZapMQ (hoje em Delphi/DataSnap) em .NET, como versão 2.0.
- Reescrever o Worker Control (serviço e Management Studio) em .NET, como versão 2.0.
- Acrescentar funcionalidades que a versão atual não tem (seção 7 e seção 11).
- Fazer tudo isso sem interromper os ambientes que usam a versão atual e sem exigir alteração de código nas aplicações que consomem o ZapMQ.

## 2. Premissas

Estas regras valem para todo o projeto. Qualquer proposta de desenho ou de código deve ser conferida contra elas.

1. **Uma mensagem é processada uma única vez.** O servidor nunca reentrega uma mensagem por conta própria. Mensagem entregue e não confirmada não volta para a fila.
2. **Trocar a DLL é suficiente.** Nenhuma aplicação consumidora precisa de alteração de código por causa de um recurso novo. A API pública dos wrappers .NET permanece compatível; recursos novos são configurados no servidor. Um recurso que dependa de parâmetro novo de quem envia entra como sobrecarga opcional.
3. **Compatibilidade com Delphi por tempo indeterminado.** O protocolo atual (DataSnap REST) continua atendido pelo servidor novo, para os clientes que usam o wrapper Delphi.
4. **Somente serviço Windows.** Não há requisito de Linux ou contêiner.
5. **O ZapMQ é um produto independente.** O painel e os recursos fazem parte do ZapMQ e não dependem de nenhum sistema que o utilize.
6. **O trace online continua existindo.** A forma de transporte pode mudar; o recurso, não.
7. **Um ambiente por vez.** Nenhuma versão é levada ao ambiente seguinte antes de estar funcionando por completo no de desenvolvimento.
8. **Workers iniciam como SYSTEM.** Após reinício do servidor, o Worker Control sobe sozinho e inicia todas as instâncias configuradas sob a conta SYSTEM, como hoje.

## 3. Decisões tomadas

| Tema | Decisão |
|---|---|
| Repositório | O mesmo. A versão Delphi fica na branch `delphi-v1`; a `main` passa a ser a 2.0 em .NET |
| Entrega | No máximo uma vez. Confirmação (ack) sem reentrega automática |
| Painel de administração | Servido pelo próprio serviço do ZapMQ, em porta própria, publicado atrás do proxy reverso já existente sob o caminho `/zapmq` |
| Console | Único: o painel do ZapMQ ganha uma seção do Worker Control, visível quando houver um Worker Control conectado |
| Configuração | Arquivo JSON, nos dois produtos |
| Dados que crescem | SQLite embutido (mensagens persistidas, histórico de eventos, contadores) |
| Autenticação e TLS no broker | Fora do escopo por enquanto |
| Login do painel | A decidir (seção 15). O painel nasce com um ponto único de verificação de acesso |

## 4. Como a versão 1.x funciona

### 4.1 Servidor

Serviço Windows em Delphi que expõe DataSnap REST por HTTP (porta padrão 5679, lida de `ZapMQ.ini`). Todo o estado fica em memória.

Os quatro métodos são chamados por `GET`, com os parâmetros no caminho da URL:

| Rota | Função | Retorno |
|---|---|---|
| `/datasnap/rest/TZapMethods/GetMessage/{fila}` | Entrega a próxima mensagem pendente da fila | JSON da mensagem, ou vazio |
| `/datasnap/rest/TZapMethods/UpdateMessage/{fila}/{json}` | Publica uma mensagem | Id da mensagem |
| `/datasnap/rest/TZapMethods/GetRPCResponse/{fila}/{id}` | Busca a resposta de um RPC | JSON da mensagem com `Response`, ou vazio |
| `/datasnap/rest/TZapMethods/UpdateRPCResponse/{fila}/{id}/{json}` | Grava a resposta de um RPC | `OK`, ou vazio |

Toda resposta vem no envelope `{"result":["<valor>"]}`.

Formato da mensagem: `Id` (texto), `Body` (objeto JSON), `RPC` (booleano), `TTL` (inteiro, milissegundos), `Response` (objeto JSON).

Ciclo de vida:

- Mensagem comum: `Pending` → entregue no `GetMessage` → `Processed` → removida em até 1 s.
- Mensagem RPC: `Pending` → `Sended` → `Processing` → `Answered` (ao receber `UpdateRPCResponse`) → `Processed` (ao ser lida por `GetRPCResponse`).
- Mensagem pendente com `TTL > 0` e idade maior que o TTL vira `Expired` e é removida.
- Qualquer mensagem em `Pending`, `Sended`, `Processing` ou `Answered` é removida 180 s após a criação.
- Fila é criada no primeiro envio e removida 1 min depois de ficar vazia.

### 4.2 Comportamentos conhecidos da 1.x

| Comportamento | Tratamento na 2.0 |
|---|---|
| A mensagem é escolhida sob lock, mas o status só muda depois; dois consumidores podem receber a mesma mensagem | Corrigido: escolha e marcação são uma única operação |
| Sem confirmação: mensagem comum é dada como processada ao ser entregue | Mantido para clientes v1; clientes v2 confirmam (seção 6) |
| Descarte fixo aos 180 s | Passa a ser a retenção padrão, configurável por fila |
| `TTL` de 16 bits: valor acima de 65.535 ms é recusado com erro e a mensagem não é publicada | Removido: o limite não existe mais, nem no protocolo v1 |
| Corpo da mensagem trafega na URL | Mantido no v1; no v2 vai no corpo |
| Sem persistência, métricas, painel ou log além de erros no Event Log | Seção 7 |
| "Exchange" citado no README nunca foi implementado | Seção 7 |

### 4.3 Wrappers

- **.NET** (`ZapMQWrapper.dll`, `netstandard2.0`, depende de `Newtonsoft.Json`): uma thread consulta todas as filas vinculadas a cada 300 ms e executa os handlers em série; uma thread por RPC pendente consulta a resposta a cada 300 ms. Contém uma proteção contra entrega duplicada baseada em objeto nomeado do Windows, válida apenas entre processos da mesma máquina.
- **Delphi**: usa o proxy cliente gerado pelo DataSnap; uma thread por nível de prioridade de fila (intervalos de 100 a 1000 ms); uma thread para todos os RPCs pendentes.
- **Distribuição**: a DLL .NET é copiada para dentro de cada projeto consumidor; o wrapper Delphi é distribuído pelo Boss, por tag.

## 5. Arquitetura da 2.0

```
            clientes v1 (wrapper Delphi, DLL .NET antiga)        clientes v2 (DLL .NET nova)
                        │  HTTP GET /datasnap/rest/...                    │  conexão permanente
                        ▼                                                 ▼
              ┌───────────────────────── serviço ZapMQ ─────────────────────────┐
              │  Camada de compatibilidade v1        Protocolo v2                │
              │                └──────────┬───────────────┘                      │
              │                     Núcleo do broker                             │
              │        filas · entrega exclusiva · RPC · exchange · retenção     │
              │                          │                                       │
              │   Persistência (SQLite, por fila)     Métricas e log             │
              │                          │                                       │
              │            API de administração  ──►  Painel web (porta própria) │
              └──────────────────────────────────────────────────────────────────┘
                                         ▲
                              Worker Control (serviço separado, cliente v2)
```

- **Plataforma:** serviço Windows em .NET, sobre o servidor web embutido (Kestrel). Alvo: a versão LTS corrente do .NET no início da implementação (o suporte ao .NET 8 termina em novembro de 2026, então o servidor não deve nascer nele). Os wrappers continuam em `netstandard2.0` e não são afetados por essa escolha.
- **Portas:** 5679 para mensageria (v1 e v2 na mesma porta, para que a troca do servidor não exija mudança em nenhum cliente); porta própria e configurável para o painel.
- **Configuração:** `appsettings.json` ao lado do executável, no padrão do .NET, com a seção `ZapMQ` (porta, retenção padrão e, mais adiante, definição de filas e exchanges). O `ZapMQ.ini` da 1.x não é lido: quem alterou a porta nele precisa repetir o valor no `appsettings.json` ao instalar a 2.0.
- **Fila não declarada:** continua sendo criada no primeiro envio, em memória, com a retenção padrão. Declarar a fila só é necessário para mudar o comportamento dela.
- **Núcleo sem dependência de transporte:** as duas camadas de protocolo chamam o mesmo núcleo, o que permite testar as regras do broker sem HTTP.
- **Limitações da 1.x ficam na camada v1:** validação de campos obrigatórios, limite de 16 bits do TTL, resposta de RPC antes da entrega, formato de erro e de escape do DataSnap existem só em `src/ZapMQ.Server/V1`. O núcleo e o protocolo v2 não herdam nenhuma delas.

### Registros

| Tipo | Onde | Uso |
|---|---|---|
| Log de diagnóstico do serviço (início, parada, chamadas recusadas, erros) | Arquivo diário em `logs/`, com retenção configurável; avisos e erros também no Event Log | Descobrir por que o serviço falhou, mesmo que o banco esteja indisponível |
| Histórico de mensagens e eventos por fila | SQLite | Consulta e filtro no painel |

### Instalação

Não há instalador. `dotnet publish src/ZapMQ.Server -c Release -r win-x64 -o publish/win-x64` gera um único `ZapMQ.exe` que carrega o runtime do .NET, de modo que o servidor de destino não precisa de nada instalado. Os dois arquivos gerados (`ZapMQ.exe` e `appsettings.json`) são copiados para uma pasta e o serviço é registrado com `sc.exe`. O passo a passo de configuração, instalação, convivência com a 1.x, atualização e remoção está no `README.md`.

Em servidor com Microsoft Defender configurado para bloquear executáveis desconhecidos, o `ZapMQ.exe` novo é impedido de rodar ("Access is denied") até a pasta ou o arquivo constar nas exceções definidas pela administração do servidor.

## 6. Semântica de entrega

1. Cada mensagem é entregue a um único consumidor. A seleção e a marcação são atômicas.
2. O servidor nunca reentrega por conta própria.
3. Cliente v1: a entrega encerra a mensagem, como hoje.
4. Cliente v2: a mensagem fica "em processamento" até o wrapper confirmar. O wrapper confirma sozinho quando o handler termina (respeitando o `pProcessing`); a aplicação não muda.
5. Mensagem entregue e não confirmada dentro do prazo da fila vai para a fila de mensagens mortas com o motivo "entregue, sem confirmação". Reenviar é uma ação manual no painel.
6. Mensagem expirada antes de ser entregue vai para a fila de mensagens mortas com o motivo "expirada".
7. RPC: a resposta é entregue uma única vez a quem enviou a pergunta.

Consequência: se um consumidor cair no meio do processamento, a mensagem não é processada de novo automaticamente. Ela deixa de sumir sem rastro e passa a ficar visível para decisão manual.

## 7. Funcionalidades do ZapMQ por versão

| Versão | Conteúdo | Critério de pronto |
|---|---|---|
| **2.0** | Servidor .NET; camada de compatibilidade v1; entrega exclusiva; log estruturado em arquivo e Event Log; contadores por fila; endpoints de saúde e métricas; TTL e retenção configuráveis | Passa nos testes de contrato gravados contra o servidor Delphi; wrappers atuais (.NET e Delphi) funcionam sem recompilar |
| **2.1** | Protocolo v2 com entrega por push; confirmação sem reentrega; fila de mensagens mortas; wrapper .NET 2.0 | Aplicação existente funciona só com a troca da DLL, contra servidor novo e contra servidor antigo |
| **2.2** | Painel web; exchange (fanout) | Painel acessível atrás do proxy em `/zapmq`; envio para um exchange distribui cópias sem mudança no remetente |
| **2.3** | Persistência por fila em SQLite; mensagens com atraso | Fila durável sobrevive a reinício do serviço |
| **Sob demanda** | Prioridade entre filas e entre mensagens | — |
| **Fora** | Autenticação e TLS no broker | — |

Detalhes por funcionalidade:

- **TTL e retenção:** três prazos independentes — validade da mensagem (TTL), tempo máximo na fila (retenção, padrão 180 s) e tempo de espera do RPC.
- **Push:** o wrapper mantém uma conexão permanente; o servidor envia a mensagem quando ela chega e devolve a resposta de RPC pelo mesmo canal. O servidor passa a saber quais clientes estão conectados e em quais filas.
- **Mensagens mortas:** uma fila `<fila>.dead` por fila de origem, com motivo e horários originais, e limite de tamanho e de tempo.
- **Exchange:** um exchange distribui uma cópia da mensagem para cada fila ligada a ele. Como o destino são filas comuns, clientes v1 também consomem. Quem publica usa `SendMessage` com o nome do exchange.
- **Persistência:** opção por fila. Filas não duráveis continuam só em memória.
- **Atraso:** a mensagem leva um horário de entrega e fica invisível até lá. Depende de sobrecarga nova no envio. Agendamento recorrente não faz parte do broker.

## 8. Wrappers

### 8.1 Wrapper .NET 2.0

Permanecem idênticos: nome do assembly (`ZapMQWrapper.dll`), namespace (`ZapMQ`), alvo (`netstandard2.0`), dependência (`Newtonsoft.Json`) e toda a superfície pública:

- `ZapMQWrapper(host, port)`, `Bind`, `UnBind`, `IsBinded`, `SendMessage`, `SendRPCMessage`, `StopThreads`, `PendingRPCCount`
- `OnRPCExpired`, `DefaultRPCTimeout`
- `DeduplicateMessages`, `OnDuplicateDiscarded`, `OnClaimFailure` (continuam existindo; deixam de ter efeito quando o servidor é 2.0)
- `ZapJSONMessage`, `ZapMQHandler`, `ZapMQHandlerRPC` e os demais delegates

Comportamento:

- **Negociação:** ao conectar, tenta o protocolo v2; se o servidor for 1.x, usa DataSnap. Servidor e DLLs podem ser trocados em qualquer ordem.
- **Ritmo de processamento:** uma mensagem por vez por instância do wrapper, respeitando `pProcessing`, como hoje.
- **Reconexão:** automática, sem perder os vínculos de fila.
- **Distribuição:** pacote NuGet, além da DLL avulsa para quem a referencia direto.

### 8.2 Wrapper Delphi

Permanece como está, usando a camada de compatibilidade v1. Só será reescrito para o protocolo v2 se algum projeto Delphi precisar de push ou de confirmação.

## 9. Painel de administração

- **Hospedagem:** servido pelo serviço do ZapMQ em porta própria; caminho base configurável para funcionar sob `/zapmq` atrás de proxy reverso; o proxy precisa repassar a conexão permanente usada pelas atualizações ao vivo.
- **Conteúdo do ZapMQ:** filas (pendentes, em processamento, vazão), clientes conectados, conteúdo de mensagens, mensagens mortas (inspecionar, reenviar, descartar), exchanges, ações de limpar e pausar fila.
- **Conteúdo do Worker Control:** seção 11.
- **Acesso:** ponto único de verificação, inicialmente liberado. Enquanto o login não for definido, a restrição fica a cargo do proxy (IP ou senha básica).
- **Limite conhecido:** o painel não reinicia o próprio ZapMQ; se o ZapMQ parar, o painel para junto.

## 10. Worker Control 1.x — como funciona

- Serviço Windows que lê `ConfigWorkers.json` a cada `RateLoadConfig` e mantém uma thread por grupo.
- Cada grupo garante `TotalWorkers` processos do executável configurado, criados ocultos, sob a conta do serviço (SYSTEM).
- **Keep-alive:** RPC para a fila `<pid>` a cada `MonitoringRate`, com prazo `TimeoutKeepAlive`. Sem resposta, o processo é encerrado e reposto no ciclo seguinte.
- **Safe stop:** mensagem para a fila `<pid>SS`.
- **Trace:** o Management Studio abre uma porta TCP local e pede ao worker, por RPC na fila `<pid>TR`, que se conecte a ela; o worker envia o texto de `Trace()` por esse socket.
- **Administração:** fila `WorkerControlAdmin`, com os comandos `CurrentWorkers` e `ReloadConfig`.
- **Boost:** janela de horário que soma `BoostWorkers` ao grupo.

Fragilidades a corrigir:

| Hoje | Na 2.0 |
|---|---|
| Queda de processo só é notada pelo keep-alive | O serviço observa o processo e repõe na hora |
| Safe stop não confere se o processo saiu | Espera a saída; passado o prazo, encerra à força |
| Lista de workers só em memória; reinício do serviço duplica instâncias | Ao subir, reconhece os workers que já estão rodando |
| Reinício do ZapMQ faz keep-alives expirarem e workers saudáveis serem mortos | Perda de conexão com o broker não conta como worker travado |
| Reinício em laço sem limite nem registro | Espera crescente após N quedas e grupo sinalizado |
| Fim do boost depende da recarga do arquivo; não cruza meia-noite | Boost avaliado continuamente, com várias janelas |
| Management Studio só funciona na máquina do serviço; trace limitado a 10 workers | Painel web; trace sem limite |

## 11. Worker Control 2.0

**Compatibilidade**

- Mesmas filas (`<pid>`, `<pid>SS`, `<pid>TR`, `WorkerControlAdmin`) e mesmo formato de `ConfigWorkers.json`. Worker com a DLL antiga continua sendo monitorado.
- Wrapper de worker .NET com a mesma API: `WorkerWrapperCore(host, port, keepAlive, safeStop)` e `Trace(texto)`.
- Sem instalador, como o ZapMQ: executável único, registrado com `sc.exe`, com o passo a passo no `README.md` do repositório.
- Serviço Windows com início automático, conta SYSTEM, dependente do serviço ZapMQ. Ao subir, espera o ZapMQ responder antes de avaliar keep-alive.

**Armazenamento**

- Configuração em `ConfigWorkers.json`. Só o serviço grava o arquivo (gravação atômica, com cópia da versão anterior). Edição manual continua possível e é detectada na hora.
- Histórico de eventos, contadores de reinício e medições de saúde em SQLite.

**Funcionalidades**

| Funcionalidade | Descrição |
|---|---|
| Detecção imediata de queda | Reposição sem esperar o keep-alive |
| Safe stop com confirmação | Prazo configurável antes do encerramento forçado |
| Recuperação de órfãos | Reconhece workers existentes ao subir |
| Proteção contra reinício em laço | Espera crescente e sinalização do grupo |
| Histórico de eventos | Subidas, quedas, encerramentos por prazo e paradas |
| Saúde por worker | Tempo no ar, CPU, memória, último keep-alive |
| Ações manuais | Reiniciar worker ou grupo; mudar a quantidade na hora |
| Boost melhorado | Várias janelas, dias da semana, janela que cruza a meia-noite |
| Escala pela fila | Sobe workers quando a fila do grupo acumula |
| Reciclagem programada | Reinício do grupo em horário definido |
| Parâmetros de execução | Argumentos e pasta de trabalho por grupo |
| Trace melhorado | Sem limite de workers, acesso remoto, busca, pausa, exportação e histórico curto |

**Trace na 2.0**

- A chamada `Trace(texto)` nas aplicações não muda; ligar e desligar continua sob demanda, por worker.
- O texto passa pela conexão do ZapMQ (worker → servidor → painel) em vez de um socket direto, o que permite acompanhar de outra máquina.
- O trace só trafega enquanto alguém assiste e é descartável: se o painel não acompanhar o volume, linhas são descartadas em vez de acumular no broker.
- Workers em Delphi ou com a DLL antiga: o serviço do Worker Control abre a porta TCP no lugar do Management Studio e repassa o texto ao painel.

**Seção no painel**

Grupos e workers com estado ao vivo, edição da configuração, ações manuais, histórico de eventos, trace por worker e parar/iniciar o serviço do Worker Control.

## 12. Estrutura do repositório

| Onde | Conteúdo |
|---|---|
| Branch `delphi-v1` | Versão Delphi 1.x, em manutenção |
| Branch `main` | Versão 2.0 em .NET |
| `src/` | Projetos .NET: núcleo do broker, compatibilidade v1, protocolo v2, persistência, serviço e painel |
| `tests/` | Testes do núcleo e testes de contrato do protocolo v1 |
| `docs/` | Este plano e a especificação do protocolo v2 |
| `README.md` | Configuração, instalação, atualização e remoção do serviço |

Os fontes Delphi saem da `main` quando o código .NET entrar; continuam disponíveis na `delphi-v1`. O Worker Control segue o mesmo modelo no repositório dele.

## 13. Ordem de execução

| Fase | Entrega | Depende de |
|---|---|---|
| 0 | Testes de contrato gravados contra o servidor Delphi em execução (respostas exatas dos quatro métodos, incluindo casos de erro e de codificação) | — |
| 1 | Núcleo do broker em .NET com testes próprios | — |
| 2 | Camada de compatibilidade v1 passando nos testes da fase 0; log, métricas, TTL e retenção | 0, 1 |
| 3 | Validação com clientes reais: DLL .NET atual e wrapper Delphi contra o servidor novo, em ambiente de desenvolvimento | 2 |
| 4 | Troca do servidor nos ambientes (versão 2.0) | 3 |
| 5 | Especificação e implementação do protocolo v2; wrapper .NET 2.0; confirmação e mensagens mortas (versão 2.1) | 4 |
| 6 | Painel e exchange (versão 2.2) | 5 |
| 7 | Persistência e atraso (versão 2.3) | 5 |
| 8 | Worker Control 2.0: serviço, wrapper de worker e seção no painel | 5, 6 |

A troca do servidor (fase 4) é parar um serviço e iniciar o outro na mesma porta. Como a 1.x não persiste nada, as mensagens em trânsito no momento da troca se perdem; voltar atrás é o mesmo procedimento no sentido inverso.

### Andamento

| Fase | Situação |
|---|---|
| 0 | Feita. `tests/contract/contract.py` grava como um servidor responde ao protocolo 1.x; `tests/contract/delphi-1.x.json` guarda 89 respostas de um servidor Delphi real |
| 1 | Feita: `src/ZapMQ.Core`, com testes em `tests/ZapMQ.Core.Tests` |
| 2 | Feita: `src/ZapMQ.Server` com a camada de compatibilidade v1, retenção configurável, `/health`, `/metrics` e log diário em arquivo. A instalação é manual, descrita no `README.md` |
| 3 | Feita. O wrapper .NET 1.x roda sem modificação nos testes (`tests/ZapMQ.Server.Tests`), e a primeira instalação mostrou serviços .NET e clientes Delphi (via wrapper Delphi) publicando, consumindo e fazendo RPC contra o servidor novo |
| 4 | Feita no ambiente de desenvolvimento em 2026-10-07. Demais ambientes pendentes |
| 5 | Feita no ambiente de desenvolvimento em 2026-10-07: servidor 2.1.0 e wrapper .NET 2.0 em todas as aplicações .NET (serviços e APIs), que passaram a ser publicadas em pasta, com a DLL do wrapper solta. Em uso real, com um serviço Delphi em v1 na mesma instalação. Especificação em [`PROTOCOLO-V2.md`](PROTOCOLO-V2.md). Faltam o teste contra um servidor Delphi 1.x de verdade e o teste em .NET Framework 4.8 |
| 8 | Iniciada. Especificação proposta em [`ESPECIFICACAO-2.0.md`](https://github.com/MurilloLazzaretti/Worker-Control/blob/main/docs/ESPECIFICACAO-2.0.md), no repositório do Worker Control. Por decisão de 2026-10-07, nada vai a outro ambiente antes de o Worker Control 2.0 e o painel estarem funcionando no de desenvolvimento, o que traz a fase 8 para antes da 7 |

Para repetir a comparação: `contract.py record http://host:porta saida.json` contra o servidor novo e `contract.py compare delphi-1.x.json saida.json`.

O que a gravação mostrou sobre a 1.x, e que a camada v1 reproduz:

- `Id`, `RPC` e `TTL` são obrigatórios em `UpdateMessage`; a falta de um deles é erro (`Value 'X' not found`).
- `TTL` negativo ou fracionário é erro (`'N' is not a valid integer value`) e a mensagem não é publicada. A 1.x também recusava valores acima de 65.535; isso não foi mantido.
- `UpdateRPCResponse` é aceito para qualquer mensagem ainda na fila, mesmo não entregue; a mensagem passa a contar como respondida e não é mais entregue.
- Uma barra não escapada dentro do JSON é lida como parâmetro a mais e a chamada é recusada; um `?` é lido como início de query string.
- Só `GET` chega a um método; nomes de método não diferenciam maiúsculas.
- O cliente DataSnap do Delphi termina toda URL com uma barra, que não conta como parâmetro. Isso não apareceu na gravação, feita com requisições montadas à mão, e só foi descoberto na primeira instalação. Os cenários `trailing_slash.*` do roteiro ainda precisam ser gravados contra um servidor Delphi.

Diferenças intencionais em relação à 1.x:

- `TTL` acima de 65.535 ms é aceito. Na 1.x a publicação falhava com erro.
- Mensagem vencida não é entregue nem no intervalo de até 1 s em que a 1.x ainda a entregaria.

## 14. Riscos

| Risco | Tratamento |
|---|---|
| O cliente DataSnap do Delphi depende de detalhes de sessão, cabeçalhos e formato de erro que a camada de compatibilidade precisa reproduzir | Fase 0 grava o comportamento real; fase 3 valida com clientes Delphi compilados no Windows |
| O wrapper Delphi converte o corpo da mensagem com codificação ASCII, o que pode corromper acentos | Verificar na fase 0; o servidor novo deve responder como o atual para não mudar o que o cliente recebe |
| A confirmação muda o destino de mensagens cujo consumidor cai: hoje somem, na 2.1 passam a acumular em mensagens mortas | Limite de tamanho e de tempo por fila morta; indicador no painel |
| O painel depende do ZapMQ estar no ar | O Worker Control repõe processos sem depender do broker, grava log em arquivo e aceita edição manual do JSON |
| Versões do painel e do Worker Control andam juntas | Contrato de administração versionado entre os dois |
| Volume de trace disputando recurso com mensagens | Trace descartável e ativo só sob demanda |

## 15. Em aberto

1. Forma de login e de controle de acesso do painel.
2. Tecnologia da interface do painel.
3. Porta padrão do painel.
5. Publicar uma Release no GitHub para a última versão Delphi.
