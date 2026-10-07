# ZapMQ — Protocolo v2

Situação: aprovada em 2026-10-07. Implementação em andamento.
Última revisão: 2026-10-07.

Este documento especifica o protocolo v2 do ZapMQ e o que ele exige do servidor e do wrapper .NET. Corresponde à fase 5 do [plano](PLANO-2.0.md) (versão 2.1): entrega por push, confirmação sem reentrega e mensagens mortas.

O protocolo v1 (DataSnap) continua existindo ao lado deste, sem alteração.

---

## 1. Objetivos e limites

**O que o v2 resolve**

- Entrega imediata, sem consulta periódica.
- O servidor passa a saber quando o processamento de uma mensagem terminou.
- Mensagem que não chegou a ser processada deixa de sumir sem rastro.
- O servidor passa a saber quem está conectado e em quais filas.
- O corpo da mensagem deixa de trafegar na URL.

**O que o v2 não muda**

- Uma mensagem é entregue a um único consumidor e nunca é reentregue automaticamente.
- A aplicação que usa o wrapper não muda uma linha: mesma DLL, mesmas classes, mesmos métodos.
- Filas continuam sendo criadas no primeiro envio.
- Um mesmo wrapper continua processando uma mensagem por vez.

**Fora deste documento**

Painel, exchange, persistência, atraso, prioridade e o trace do Worker Control. O protocolo reserva espaço para eles (seção 10), mas eles são especificados nas versões em que entram.

## 2. Transporte

- **WebSocket** na mesma porta do serviço (padrão 5679), no caminho `/v2`.
- Quadros de texto, um objeto JSON por quadro, UTF-8.
- A conexão é permanente e bidirecional. Cada instância do wrapper mantém uma conexão.
- Tamanho máximo de um quadro: 4 MB por padrão, configurável.

O WebSocket foi escolhido porque existe pronto no `netstandard2.0` (sem dependência nova no wrapper), tem implementação disponível em Delphi caso um dia seja necessário e atravessa um proxy reverso comum.

### 2.1 Batimento

O servidor envia um ping do WebSocket a cada 15 segundos. Sem resposta em 30 segundos, a conexão é considerada perdida. O wrapper faz o mesmo no sentido contrário e, ao perder a conexão, reconecta (seção 7.3).

## 3. Formato dos quadros

**Pedido** (de quem inicia, cliente ou servidor):

```json
{ "op": "publish", "id": 17, "queue": "Pedidos", "body": { "numero": 42 } }
```

**Resposta** a um pedido:

```json
{ "re": 17, "ok": true, "messageId": "{3F2504E0-4F89-11D3-9A0C-0305E82C3301}" }
{ "re": 17, "ok": false, "error": { "code": "queue-required", "message": "Inform the Queue name" } }
```

- `op`: operação.
- `id`: número escolhido por quem envia, único dentro da conexão. Aparece em `re` na resposta.
- Avisos do servidor que não esperam resposta não têm `id`.
- Campos desconhecidos são ignorados; operação desconhecida recebe o erro `unknown-op`.

## 4. Operações

### 4.1 `hello` — cliente → servidor

Primeiro quadro de toda conexão. Qualquer outra operação antes dele encerra a conexão.

```json
{ "op": "hello", "id": 1, "protocol": 2,
  "client": { "name": "Pedidos.Worker.exe", "pid": 4812, "host": "SRV01", "wrapper": "dotnet/2.0.0" } }
```

```json
{ "re": 1, "ok": true, "protocol": 2, "server": "2.1.0", "connection": "c-000042" }
```

Os dados de `client` são preenchidos pelo wrapper sozinho. Servem para o painel e para o log; não são credencial.

### 4.2 `bind` e `unbind` — cliente → servidor

```json
{ "op": "bind", "id": 2, "queue": "Pedidos" }
{ "op": "unbind", "id": 9, "queue": "Pedidos" }
```

Depois do `bind`, o servidor passa a entregar mensagens dessa fila a esta conexão. Uma conexão pode vincular várias filas. O `unbind` não cancela a mensagem que já foi entregue e aguarda confirmação.

### 4.3 `publish` — cliente → servidor

```json
{ "op": "publish", "id": 3, "queue": "Pedidos", "body": { "numero": 42 }, "rpc": false, "ttlMs": 30000 }
```

| Campo | Obrigatório | Descrição |
|---|---|---|
| `queue` | sim | Nome da fila, sensível a maiúsculas |
| `body` | não | Objeto JSON. Qualquer outro valor vira `{}`, como no v1 |
| `rpc` | não | `true` quando quem envia espera resposta. Padrão `false` |
| `ttlMs` | não | Validade em milissegundos. `0` ou ausente: sem validade própria; vale a retenção da fila. Sem limite superior |

A resposta traz o `messageId` gerado pelo servidor. O `publish` só responde depois de a mensagem estar na fila.

### 4.4 `deliver` — servidor → cliente

```json
{ "op": "deliver", "queue": "Pedidos",
  "message": { "id": "{3F25...}", "body": { "numero": 42 }, "rpc": false } }
```

O servidor só envia um `deliver` a uma conexão que não tenha outra mensagem aguardando confirmação (seção 5.2).

### 4.5 `ack` — cliente → servidor

```json
{ "op": "ack", "id": 4, "queue": "Pedidos", "messageId": "{3F25...}" }
```

Informa que o processamento terminou. O wrapper envia sozinho quando o handler retorna. Para mensagem RPC cujo handler devolveu resposta, usa-se `respond` no lugar do `ack`.

### 4.6 `respond` — cliente → servidor

```json
{ "op": "respond", "id": 5, "queue": "Pedidos", "messageId": "{3F25...}", "response": { "aceito": true } }
```

Grava a resposta de uma mensagem RPC e vale como confirmação. Só é aceito da conexão que recebeu a mensagem.

### 4.7 `response` — servidor → cliente

```json
{ "op": "response", "queue": "Pedidos", "messageId": "{3F25...}",
  "message": { "id": "{3F25...}", "body": { "numero": 42 }, "rpc": true, "response": { "aceito": true } } }
```

Enviado à conexão que publicou a mensagem RPC, assim que a resposta chega. A resposta é entregue uma única vez.

### 4.8 `await` — cliente → servidor

```json
{ "op": "await", "id": 6, "queue": "Pedidos", "messageIds": ["{3F25...}", "{9A0C...}"] }
```

Usado só depois de uma reconexão: o wrapper informa de quais RPCs ainda espera resposta, e o servidor passa a enviar os `response` correspondentes para a conexão nova, inclusive os que chegaram enquanto ela estava fora.

### 4.9 `bye` — servidor → cliente

```json
{ "op": "bye", "reason": "shutting-down" }
```

Avisa que o servidor vai encerrar a conexão. O wrapper reconecta.

### 4.10 Erros

| Código | Quando |
|---|---|
| `invalid-request` | Quadro que não é JSON, sem `op` ou com campo de tipo errado |
| `unknown-op` | Operação que o servidor não conhece |
| `hello-required` | Operação enviada antes do `hello` |
| `unsupported-protocol` | `protocol` que o servidor não atende |
| `queue-required` | Nome de fila vazio |
| `too-large` | Quadro acima do tamanho máximo |
| `not-found` | `ack`, `respond` ou `await` para mensagem que não existe ou não pertence a esta conexão |

## 5. Regras de entrega

### 5.1 Escolha do consumidor

Ao chegar uma mensagem, o servidor a entrega à primeira conexão vinculada àquela fila que esteja livre (sem mensagem aguardando confirmação), em rodízio entre as conexões. Se nenhuma estiver livre, a mensagem fica pendente e sai quando uma conexão liberar ou quando um cliente v1 consultar a fila.

Clientes v1 e v2 podem consumir a mesma fila. A escolha da mensagem é uma operação única no servidor, então ela vai para um só consumidor em qualquer combinação.

### 5.2 Uma mensagem por vez

Cada conexão tem no máximo uma mensagem entregue e não confirmada, considerando todas as filas vinculadas. É o comportamento do wrapper atual, que processa uma mensagem por vez em todas as suas filas.

### 5.3 Estados de uma mensagem

```
publicada ──► pendente ──► entregue ──► confirmada            (some)
                 │             │
                 │             ├──► respondida ──► resposta coletada   (RPC; some)
                 │             │
                 │             └──► não confirmada   ──► mensagens mortas
                 │                                       (ou de volta à fila, se a fila permitir)
                 │
                 ├──► vencida (TTL)        ──► mensagens mortas
                 └──► não consumida (retenção) ──► mensagens mortas
```

### 5.4 Mensagem entregue e não confirmada

Se a conexão cai antes do `ack` ou do `respond`, a mensagem **não volta para a fila**. Ela vai para as mensagens mortas com o motivo `unconfirmed`, e reenviá-la é uma decisão manual.

O servidor não tem como saber se o handler chegou a executar, total ou parcialmente. Reentregar poderia processar a mesma mensagem duas vezes, o que a regra do ambiente proíbe.

Consequência: uma queda de rede no instante da entrega coloca a mensagem nas mensagens mortas mesmo que o consumidor nem a tenha recebido. A mensagem fica visível e recuperável, mas depende de alguém reenviá-la.

Não há prazo de confirmação enquanto a conexão estiver viva: um handler demorado não perde a mensagem.

Uma fila pode ser marcada com `RedeliverUnconfirmed`. Nesse caso a mensagem não confirmada volta para o início da fila e é entregue de novo. A opção vem desligada e só serve para filas em que processar a mesma mensagem duas vezes não causa dano.

### 5.5 RPC

- A resposta é devolvida a quem publicou, pela conexão em que publicou.
- O tempo de espera continua sendo controlado pelo wrapper (`DefaultRPCTimeout` ou o TTL informado), que dispara `OnRPCExpired` como hoje.
- RPC entregue e nunca respondido é descartado pela retenção e contado nas métricas. Não vai para as mensagens mortas.

## 6. Mensagens mortas

Guardam o que deixou de ser entregue ou confirmado, com o motivo.

| Motivo | Quando |
|---|---|
| `expired` | Venceu o TTL antes de ser entregue |
| `not-consumed` | Ninguém consumiu dentro da retenção da fila |
| `unconfirmed` | Foi entregue por v2 e a conexão caiu antes da confirmação |

Vale para mensagens publicadas por v1 ou por v2. Uma mensagem entregue a um cliente v1 é dada como concluída na entrega, como hoje, e nunca fica `unconfirmed`.

**Não é uma fila.** Nenhum cliente consegue vincular-se às mensagens mortas e consumi-las por engano. O acesso é só pela administração:

| Rota | Função |
|---|---|
| `GET /admin/dead-letters` | Resumo por fila: quantidade por motivo |
| `GET /admin/dead-letters/{fila}` | Lista as mensagens mortas da fila, da mais recente para a mais antiga |
| `GET /admin/dead-letters/{fila}/{id}` | Uma mensagem, com corpo, motivo, horários e, se houver, o cliente que a recebeu |
| `POST /admin/dead-letters/{fila}/{id}/requeue` | Publica uma cópia na fila de origem e remove a original |
| `DELETE /admin/dead-letters/{fila}/{id}` | Descarta |
| `DELETE /admin/dead-letters/{fila}` | Descarta todas as da fila |

A cópia reenviada recebe um id novo e guarda o id da original.

**Limites**, por fila: 1.000 mensagens e 7 dias; o que passar disso é descartado, começando pelas mais antigas. Configurável; `0` em qualquer dos dois desliga o armazenamento para aquela fila.

Até a versão com persistência (2.3), as mensagens mortas ficam em memória e se perdem ao reiniciar o serviço.

## 7. Wrapper .NET 2.0

### 7.1 O que não muda

Nome do assembly (`ZapMQWrapper.dll`), namespace (`ZapMQ`), alvo (`netstandard2.0`), dependência (`Newtonsoft.Json`) e toda a superfície pública. Uma aplicação compilada contra a DLL 1.x funciona com a 2.0 colocada no lugar.

| Chamada da aplicação | Em v2 |
|---|---|
| `new ZapMQWrapper(host, port)` | Abre a conexão em segundo plano e envia `hello` |
| `Bind(fila, handler)` | `bind`. O handler é chamado a cada `deliver`, um por vez |
| `UnBind(fila)` | `unbind` |
| `IsBinded(fila)` | Resposta local, como hoje |
| `SendMessage(fila, corpo, ttl)` | `publish`; devolve `true` quando o servidor confirma |
| `SendRPCMessage(fila, corpo, handler, ttl)` | `publish` com `rpc`; o handler é chamado quando chega o `response` |
| `OnRPCExpired`, `DefaultRPCTimeout` | Controlados pelo wrapper, como hoje |
| `PendingRPCCount()` | Quantos RPCs aguardam resposta |
| `StopThreads()` | Fecha a conexão |
| `DeduplicateMessages`, `OnDuplicateDiscarded`, `OnClaimFailure` | Continuam existindo e não fazem nada em v2: o servidor garante a entrega única |

Regras que continuam iguais: não é permitido enviar para uma fila vinculada pelo próprio wrapper; nome de fila vazio é erro; `Body` e `Response` chegam ao handler nos mesmos tipos de hoje.

### 7.2 Escolha do protocolo

1. Ao iniciar, o wrapper tenta abrir o WebSocket em `/v2`.
2. Se o servidor responder que não conhece esse caminho, é um servidor 1.x: o wrapper trabalha em v1, exatamente como a DLL atual.
3. Em v1, ele tenta o `/v2` de novo a cada minuto. Quando o servidor for atualizado, o wrapper passa para v2 sozinho, sem reiniciar a aplicação.
4. Se o servidor não responder de forma alguma, o wrapper continua tentando.

Assim o servidor e as DLLs podem ser trocados em qualquer ordem.

### 7.3 Reconexão

- Tentativas com espera crescente, de 250 ms até 5 s.
- Ao reconectar: `hello`, `bind` de todas as filas vinculadas e `await` dos RPCs pendentes.
- A mensagem que estava em processamento quando a conexão caiu continua sendo processada pelo handler até o fim. O servidor já a registrou como `unconfirmed`; a resposta ou confirmação tardia é recusada com `not-found` e o wrapper registra o fato.
- `SendMessage` e `SendRPCMessage` chamados sem conexão esperam até 5 segundos por ela e então devolvem `false`, que é o que a aplicação já recebe hoje quando o servidor está fora.

### 7.4 Threads

- Uma thread de consumo por instância do wrapper chama os handlers de fila em série, como hoje.
- Handlers de resposta de RPC e `OnRPCExpired` são chamados fora da thread de consumo, como hoje.

### 7.5 Distribuição

Pacote NuGet e DLL avulsa. A DLL 2.0 contém o cliente v1 dentro dela, para o caso do servidor 1.x.

## 8. Servidor

### 8.1 Configuração

Novas chaves em `appsettings.json`. Todas opcionais.

```json
{
  "ZapMQ": {
    "V2": {
      "MaxFrameBytes": 4194304,
      "PingSeconds": 15,
      "PingTimeoutSeconds": 30
    },
    "DeadLetters": {
      "MaxMessagesPerQueue": 1000,
      "MaxAgeHours": 168
    },
    "Queues": {
      "Pedidos": {
        "RetentionSeconds": 3600,
        "DeadLetters": { "MaxMessagesPerQueue": 5000 }
      }
    }
  }
}
```

A seção `Queues` é o primeiro uso da definição de fila prevista no plano. Fila que não aparece nela usa os valores gerais.

| Chave de uma fila | Padrão | Descrição |
|---|---|---|
| `RetentionSeconds` | o geral | Tempo máximo de uma mensagem não consumida na fila |
| `RedeliverUnconfirmed` | `false` | Devolve à fila a mensagem entregue e não confirmada |
| `DeadLetters.MaxMessagesPerQueue` | o geral | Limite de mensagens mortas guardadas |
| `DeadLetters.MaxAgeHours` | o geral | Idade máxima de uma mensagem morta |

As definições de fila podem ser alteradas com o serviço rodando, pelas rotas `GET` e `PUT /admin/queues/{fila}`. Na 2.1 a alteração feita por essas rotas vale até o serviço reiniciar, quando volta o que está no `appsettings.json`. A partir da 2.2 o painel edita essas definições e o serviço as grava em arquivo próprio.

### 8.2 Métricas e log

- `/metrics` ganha, por fila: mensagens em processamento, confirmadas e mortas por motivo; e a lista de conexões v2 com nome do cliente, processo, máquina e filas vinculadas.
- O log registra conexão e desconexão de cliente (informação) e cada mensagem `unconfirmed` (aviso), com fila, id e cliente.

### 8.3 Encerramento

Ao parar, o serviço envia `bye` às conexões e espera até 5 segundos pelas confirmações em andamento. O que não for confirmado nesse prazo é registrado no log como `unconfirmed`; como as mensagens mortas ainda estão em memória, elas se perdem com o serviço.

## 9. Convivência com o v1

| Situação | Comportamento |
|---|---|
| Publica por v1, consome por v2 | Entrega por push, com confirmação |
| Publica por v2, consome por v1 | O cliente v1 recebe na consulta; a entrega encerra a mensagem |
| RPC publicado por v2, respondido por v1 | A resposta chega ao publicador por `response` |
| RPC publicado por v1, respondido por v2 | O publicador coleta a resposta por `GetRPCResponse`, como hoje |
| Consumidores v1 e v2 na mesma fila | O v2 livre recebe na hora; o v1 recebe o que estiver pendente quando consultar |

## 10. Reservado para as próximas versões

| Recurso | Como entra sem quebrar o v2 |
|---|---|
| Atraso | Campo `delayMs` no `publish` |
| Prioridade | Campo `priority` no `publish` e no `bind` |
| Exchange | Só no servidor: `publish` para um nome declarado como exchange distribui cópias |
| Trace do Worker Control | Operações novas, especificadas com o Worker Control |
| Autenticação | Campo novo no `hello` |

Um servidor que não conhece um desses campos o ignora; um cliente que precisa do recurso consulta a versão do servidor recebida no `hello`.

## 11. Decisões tomadas

1. **Reentrega opcional por fila.** Entra. A opção `RedeliverUnconfirmed` de uma fila, desligada por padrão, devolve a mensagem não confirmada ao início da fila em vez de enviá-la às mensagens mortas. Só deve ser ligada em filas cujo processamento repetido não causa dano. A opção tem de poder ser alterada pelo painel, com o serviço rodando.
2. **RPC não respondido não vai para as mensagens mortas.** É descartado pela retenção e contado nas métricas.
3. **Limites padrão das mensagens mortas:** 1.000 mensagens e 7 dias por fila.
4. **Rotas `/admin` na porta da mensageria durante a 2.1**, sem controle de acesso, e na porta do painel a partir da 2.2. Nenhuma versão é levada ao ambiente seguinte antes de estar funcionando por completo no de desenvolvimento.

## 12. Verificação

- Testes do núcleo para cada transição de estado da seção 5.3 e para os limites das mensagens mortas.
- Testes do servidor com um cliente WebSocket: cada operação, cada erro e a queda de conexão em cada ponto do fluxo.
- Testes de convivência v1 e v2 para cada linha da tabela da seção 9.
- Teste de entrega única com consumidores v1 e v2 concorrendo na mesma fila.
- O wrapper 2.0 contra o servidor 2.1 e contra um servidor 1.x, com a troca de protocolo sem reiniciar o processo.
- As aplicações de exemplo compiladas contra a DLL 1.x, rodando com a DLL 2.0 no lugar, sem recompilar.
- O contrato v1 (`tests/contract`) continua passando sem alteração.
