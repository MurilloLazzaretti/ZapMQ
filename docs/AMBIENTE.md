# ZapMQ — Monitoramento do ambiente

Situação: aprovada em 2026-10-08. Etapas 1, 3 e 4 implementadas, e a 5 sem o proxy no mapa; a 2 foi adiada.
Última revisão: 2026-10-08.

Este documento especifica três recursos novos do painel, que vão além da mensageria: serviços Windows, tráfego HTTP medido pelo proxy reverso e micro frontends. Continua o [painel](PAINEL.md) e a [especificação do Worker Control](https://github.com/MurilloLazzaretti/Worker-Control/blob/main/docs/ESPECIFICACAO-2.0.md).

---

## 1. Objetivo

Ver, no mesmo painel, o ambiente inteiro em que as aplicações rodam:

1. os **serviços Windows** que não são mantidos pelo Worker Control: estado, saúde, tempo no ar, reiniciar e acompanhar o log;
2. o **tráfego HTTP** de cada endpoint, medido no proxy reverso (NGINX), sem tocar nas aplicações;
3. os **micro frontends** publicados: versão de cada módulo, se está no ar e quanto é usado.

Nenhum dos três exige mudança nas aplicações monitoradas.

## 2. Decisões tomadas

| Assunto | Decisão |
|---|---|
| Quem coleta | O Worker Control, que passa a ser o agente da máquina. Não há um serviço novo |
| Vínculo com outros sistemas | Nenhum. Tudo vem de configuração: quais serviços, onde está o log do proxy, onde está o manifesto dos frontends |
| Serviço que cai | Reinício automático opcional, por serviço; o padrão é manual |
| Endereço de quem acessa | É guardado, para contar usuários distintos |
| Telas | "Aplicações" e "Worker Control" viram uma área só; a Visão geral passa a resumir o ambiente inteiro |
| Nome | O painel continua sendo o do ZapMQ, em `/zapmq` |

## 3. Arquitetura

```
              ┌──────────────────────── máquina ────────────────────────┐
navegador ──► │ ZapMQ (painel) ◄── fila WorkerControlAdmin ──► Worker Control (agente)
              │                                                  ├─ processos que ele mantém (como hoje)
              │                                                  ├─ serviços Windows
              │                                                  ├─ log de acesso do proxy reverso
              │                                                  └─ pastas dos micro frontends
              └──────────────────────────────────────────────────────────┘
```

- O painel não lê arquivo nem consulta o Windows: pede ao agente, pelo contrato de administração que já existe, com comandos novos (seção 8).
- O agente guarda o que mede no mesmo banco SQLite em que já guarda o histórico e a saúde dos workers.
- Um agente por máquina. Com serviços, proxy ou frontends em outra máquina, instala-se o Worker Control nela; o painel passa a escolher de qual agente está falando. A primeira entrega atende um agente; o contrato já leva o nome da máquina para que mais de um não exija mudança depois.
- Sem o agente no ar, as telas novas dizem isso e o resto do painel funciona, como hoje com a seção do Worker Control.

## 4. Serviços Windows

### 4.1 Cadastro

Não se digita nome de serviço. O agente lista os serviços instalados e o painel oferece a lista, com busca, para marcar os que serão acompanhados.

- A lista vem ordenada com os **sugeridos** primeiro: serviços cujo executável está dentro de uma das pastas de `Services.SuggestFrom`. Essas pastas são editadas na própria tela de adicionar serviço (ou no arquivo); vazio não sugere nenhum. Serviços do próprio Windows ficam no fim, recolhidos.
- O que é marcado vai para o `ConfigWorkers.json`, na seção `Services` (seção 9), e passa a valer na hora. Pode-se editar o arquivo à mão, como os grupos.
- O próprio serviço do Worker Control não pode ser cadastrado. O do ZapMQ pode, com as ações de parar e reiniciar desabilitadas no painel, que depende dele.

### 4.2 O que se vê e o que se faz

| Recurso | Como | Limite |
|---|---|---|
| Estado | Estado no gerenciador de serviços (rodando, parado, iniciando, parando) e tipo de início | — |
| Tempo no ar | Horário de início do processo do serviço | — |
| Saúde | Processador, memória, threads e handles do processo, com histórico e gráfico. Os processos que o serviço iniciou são somados a ele: um serviço instalado por um programa que só inicia outro (um "wrapper") é medido pelo que roda de fato | Diz se está vivo e quanto consome, não se está travado por dentro |
| Verificação | Opcional, por serviço: uma porta TCP que precisa aceitar conexão, ou uma URL que precisa responder 2xx | Só o que o serviço expõe |
| Iniciar, parar, reiniciar | Pelo gerenciador de serviços, com confirmação; quem fez fica no histórico | Parar espera até `StopTimeoutMs`; depois disso informa que não parou, e não encerra à força. Parar um serviço para também os que dependem dele, como faz o Windows |
| Queda | Evento no histórico quando o serviço para sem ter sido pedido. É queda quando o Windows registra um código de saída diferente de zero; parada limpa feita por fora do painel é registrada como parada | Um serviço que cai devolvendo código zero é visto como parada limpa |
| Reinício automático | Opcional (`AutoRestart`), desligado por padrão. Ligado, o agente inicia de novo o serviço que parou sozinho, com o mesmo recuo crescente dos grupos quando cai em sequência | Não age sobre serviço parado por alguém |
| Atividade no ZapMQ | Não se informa no cadastro: o serviço que usa o broker pelo protocolo v2 é reconhecido pelo número do processo, e aparece no mapa com as filas e a vazão (etapa 5) | Só os que usam o broker; os que usam o protocolo 1.x são vistos por endereço, sem ligação com o serviço |
| Log ao vivo | Na tela de trace: o agente acompanha o arquivo de log do serviço e publica as linhas novas. Não implementado (etapa 2, adiada) | Só serviço que grava log em arquivo |

**Log ao vivo.** `Trace()` existe só para aplicação que carrega o wrapper. Para um serviço que não vai ser alterado, o equivalente é o arquivo de log dele: informa-se a pasta e o padrão do nome (`LogFiles`, por exemplo `logs\app-*.log`), e o agente acompanha o arquivo mais recente que casa com o padrão, passando para o próximo quando o serviço troca de arquivo. As linhas seguem o mesmo caminho do trace (filas descartáveis, só enquanto alguém assiste), e a tela é a mesma: filtro, pausa, exportação. Ao abrir, vêm as últimas 200 linhas do arquivo. A codificação é detectada como no trace (UTF-8; se não for, ANSI).

### 4.3 Tela

Na área "Processos e serviços" (seção 7), os serviços aparecem em cartões como os grupos: estado, tempo no ar, processador, memória, resultado da verificação, e as ações. O menu de cada um tem Saúde (gráficos), Log ao vivo e Histórico.

## 5. Tráfego HTTP

### 5.1 De onde vêm os dados

Do log de acesso do proxy reverso, lido pelo agente. É a única fonte que cobre tudo o que passa por ele sem tocar em aplicação alguma.

O formato padrão do log não tem tempo de resposta. O proxy passa a gravar um segundo arquivo, com uma linha JSON por requisição:

```nginx
log_format zapmq escape=json
    '{"t":"$time_iso8601","ms":$msec,"ip":"$remote_addr","h":"$host",'
    '"m":"$request_method","u":"$request_uri","s":$status,"b":$body_bytes_sent,'
    '"rt":$request_time,"ua":"$upstream_addr","us":"$upstream_status",'
    '"ut":"$upstream_response_time","ref":"$http_referer"}';
access_log logs/access.log combined;
access_log logs/access.zapmq.log zapmq;
```

O arquivo de sempre continua sendo gravado. O agente lê só o novo, a partir de onde parou (guarda a posição), e o **rotaciona**: ao passar de `Traffic.RotateAtMb`, renomeia o arquivo e pede ao proxy que reabra o log, mantendo `Traffic.KeepFiles` arquivos antigos. O arquivo de formato padrão não é tocado.

### 5.2 O que é guardado

Nada do corpo, de cabeçalhos ou de parâmetros.

- **Rota normalizada.** A query string é descartada, e os trechos do caminho que são identificadores viram `{id}`: números, GUIDs, textos longos em hexadecimal, textos codificados ou com mais de 40 caracteres, e códigos em que pelo menos metade são dígitos (`VG000017`). `/api/pedidos/48213/itens?x=1` é contado como `/api/pedidos/{id}/itens`. Um arquivo é contado pela pasta e pelo tipo (`/mfe/modulo/*.js`). O que a regra geral não pega — um código curto como `P2` — é resolvido escrevendo a rota em `Traffic.Routes`, com o trecho variável entre chaves.
- **Agregado por minuto**, por rota, método, servidor virtual e instância do upstream: quantidade, por classe de status (2xx, 3xx, 4xx, 5xx), bytes, e a distribuição dos tempos em faixas fixas (o bastante para mediana, p95 e p99). Retenção: `Traffic.RetentionDays` (padrão 30). Depois de 48 horas, os minutos são consolidados em horas.
- **Limite de rotas.** No máximo `Traffic.MaxRoutes` rotas distintas por dia (padrão 2.000); o excedente é somado em "outras". Protege contra caminho aleatório vindo de varredura.
- **Endereços.** Por aplicação e por hora, o conjunto de endereços distintos, para contar pessoas (não por rota, que multiplicaria o que é guardado sem dizer muito mais). Retenção: a mesma do tráfego.
- **O que não entra.** Caminhos que começam com um dos prefixos de `Traffic.Ignore` não são contados: serve para o próprio painel, que pede os números dele a cada poucos segundos e dominaria os de todo o resto.
- **Aplicação.** É o primeiro trecho do caminho, ou os dois primeiros quando o primeiro está em `Traffic.GroupBy` (padrão `api` e `mfe`): `/api/pedidos/1` é da aplicação `api/pedidos`.
- **Conexões entregues** (resposta 101, um web socket) contam como requisição e ficam fora dos tempos: duram enquanto são usadas, e isso não é tempo de resposta.
- **Instância.** Quando o proxy tenta mais de uma, vale a que respondeu. `localhost`, `127.0.0.1` e `[::1]` são a mesma máquina e contam como uma instância só.
- **Erros recentes.** As últimas `Traffic.KeepErrors` requisições com 5xx (padrão 500), com horário, rota original sem query string, status, instância e tempo, para ver o que falhou sem abrir o arquivo.

### 5.4 Telas

Contar os arquivos servidos não diz quanto cada parte da aplicação web é usada: a aplicação carrega o ponto de entrada de todos os módulos para todo mundo, e o resto fica no cache do navegador. O que diz é a **tela de onde cada pedido partiu**, que o navegador informa (o `Referer`) e o log já grava.

- De cada requisição que diz de qual tela partiu, o agente guarda o site e o caminho da tela, sem a query string e com os identificadores trocados por `{id}`, como nas rotas. Por hora: quantos pedidos partiram de cada tela e de quantos endereços diferentes.
- O navegador só diz a tela ao próprio site dela. A outro site — uma API publicada sob outro nome — ele diz só o site, o que não informa nada e é descartado. Por isso a contagem vem dos pedidos feitos ao site da aplicação (arquivos, consultas de versão), e "pedidos" mede mais o tempo que a tela ficou aberta do que cliques.
- **Uso por módulo:** as telas que têm o nome do módulo como um dos trechos do caminho. Aparece no cartão do módulo, na tela Aplicação web, em pessoas nas últimas 24 horas.
- A tela de tráfego lista as telas mais usadas do período.
- As publicações da aplicação web aparecem nos gráficos de tráfego como linhas verticais, no instante em que aconteceram.

### 5.3 O que a tela mostra

- **Agora:** requisições por segundo, taxa de erro (4xx e 5xx separados), mediana e p95, no total.
- **Por aplicação** (o primeiro trecho do caminho que o proxy usa para rotear, ou o servidor virtual): os mesmos números, com gráfico da última hora e do último dia.
- **Endpoints:** tabela com busca e ordenação — mais chamados, mais lentos, com mais erro, e os que pioraram em relação ao mesmo horário do dia anterior. As consultas prévias do navegador (`OPTIONS`) entram nos totais e ficam fora desta tabela. O detalhe de um endpoint, com a série dele, não foi feito nesta etapa.
- **Instâncias:** para cada upstream com mais de uma, quanto cada uma recebeu, o tempo e os erros de cada uma. Uma instância que não recebe nada, ou que responde pior que a outra, aparece.
- **Erros recentes** (5.2).
- **Períodos:** última hora, 24 horas, 7 dias, 30 dias.

Tráfego de arquivos estáticos (o frontend) é mostrado separado do das APIs, para não dominar os números.

## 6. Micro frontends

### 6.1 De onde vêm os dados

De arquivos, na pasta em que o proxy serve o frontend (`Frontends[].Root`):

- o **manifesto** dos módulos (`Manifest`, por exemplo `assets/mf.manifest.json`): um objeto `nome → caminho do ponto de entrada`;
- em cada módulo, o **arquivo de versão** (`VersionFile`, por exemplo `version.json`). Dele o agente lê, quando existirem, `nome`, `build` e a lista `versions` com `version`, `date` e `descriptions`; a versão atual é a primeira da lista. Outros formatos podem ser mapeados depois; sem arquivo de versão, o módulo aparece com a data de publicação apenas.

No painel a tela se chama **Aplicação web**, e os cartões dela são os módulos.

O endereço usado para conferir se um módulo responde é `BaseUrl`. Quando o agente está na mesma máquina do proxy e o nome do site não se resolve dali, usa-se `BaseUrl` com o endereço da máquina e `Host` com o nome do site.

### 6.2 O que se vê

Por módulo:

| Informação | Como |
|---|---|
| Versão, build, data e o que mudou | Arquivo de versão |
| Publicado em | Data do ponto de entrada no disco |
| No ar | O agente pede o ponto de entrada pelo proxy (`Frontends[].BaseUrl`) e espera 200. Verificado a cada minuto |
| Íntegro | O manifesto lista o módulo e a pasta dele existe, com o ponto de entrada (senão o módulo aparece como incompleto); pasta sem entrada no manifesto também é apontada |
| Uso | Pessoas que estiveram em telas do módulo nas últimas 24 horas (seção 5.4) |
| Arquivos pedidos que não existem | 404 em arquivos do módulo: costuma ser navegador com versão antiga em cache |

E, para o conjunto:

- **Linha do tempo de publicações.** O agente percebe quando o ponto de entrada de um módulo muda e registra data, versão e build anteriores e novos; também módulo que entra ou sai do manifesto, e a publicação da aplicação em volta dos módulos (o `index.html` da raiz). A primeira leitura é uma entrada só, não uma publicação por módulo. Guardado em `frontends.json`, ao lado da configuração (as últimas 300), e como evento no histórico. Publicar sem mudar a versão é comum e não é tratado como problema: o cartão mostra a data real da publicação ao lado da versão.
- **Publicação marcada no tráfego.** Os gráficos da tela de tráfego mostram o instante de cada publicação; erro que sobe depois de publicar aparece ao lado da causa.

Saber qual versão cada usuário tem aberta exige que o frontend informe, o que é mudança nele. Fica fora desta especificação.

## 7. Telas

| Hoje | Passa a ser |
|---|---|
| Visão geral (só o broker) | **Visão geral** do ambiente: no topo, tudo o que pede atenção em qualquer parte dele, cada item levando à tela onde se resolve; depois um bloco para processos e serviços, um para tráfego e um para a aplicação web, com três números cada; e a mensageria, como era |
| Mapa | **Mapa**, com uma coluna a mais, à esquerda: o proxy e as aplicações que ele alcança. Os serviços Windows que usam o broker entram como aplicações, com o estado do serviço |
| Filas, Mensagens mortas | Uma área só, **Filas**, com as mensagens mortas em uma aba |
| Aplicações, Worker Control | **Processos e serviços**: grupos e workers, serviços Windows e conexões, em abas. Histórico e Configuração continuam nela, e passam a cobrir os serviços |
| — | **Tráfego** |
| — | **Aplicação web** |

O painel "Atenção" do mapa passa a existir também na Visão geral, e inclui serviço parado, verificação falhando, instância de upstream sem resposta e módulo fora do ar.

## 8. Contrato de administração

Comandos novos na fila `WorkerControlAdmin`. A versão do contrato passa a 2; os comandos da versão 1 não mudam. Um agente anterior responde `unknown-command`, e o painel diz que a versão instalada não tem o recurso.

| Comando | Função |
|---|---|
| `ListServices` | Serviços instalados na máquina, com o executável, o estado e se é sugerido |
| `ServiceStatus` | Os serviços cadastrados: estado, processo, tempo no ar, saúde, verificação. Vai também dentro de `Status` |
| `StartService` / `StopService` / `RestartService` | Ação sobre um serviço cadastrado |
| `StartLogTrace` / `StopLogTrace` | Acompanha o log de um serviço e publica as linhas, como `StartTrace` |
| `Traffic` | Séries e totais, por período e agrupamento |
| `TrafficRoutes` | A tabela de endpoints, com filtro e ordenação |
| `TrafficErrors` | Erros recentes |
| `Frontends` | Módulos, versões, estado e publicações |

`Health` e `Events` passam a aceitar um serviço no lugar de um grupo. Cadastrar e remover serviço é `SetConfig`, como qualquer outra mudança de configuração.

## 9. Configuração

Chaves novas no `ConfigWorkers.json`, todas opcionais. Sem elas o agente faz o que faz hoje.

```json
{
  "Services": {
    "SuggestFrom": ["D:\\Apps"],
    "Items": [
      {
        "Name": "MeuServico",
        "AutoRestart": false,
        "StopTimeoutMs": 30000,
        "LogFiles": "D:\\Apps\\meu-servico\\logs\\app-*.log",
        "Check": { "Tcp": "localhost:9100" }
      }
    ]
  },
  "Traffic": {
    "AccessLog": "C:\\nginx\\logs\\access.zapmq.log",
    "ReopenCommand": "C:\\nginx\\nginx.exe -s reopen",
    "RotateAtMb": 100,
    "KeepFiles": 5,
    "RetentionDays": 30,
    "MaxRoutes": 2000,
    "KeepErrors": 500,
    "GroupBy": ["api", "mfe"],
    "Ignore": ["/zapmq/"],
    "Routes": ["/api/pedidos/{codigo}/itens"]
  },
  "Frontends": [
    {
      "Name": "Aplicação",
      "Root": "D:\\www\\app",
      "BaseUrl": "http://app.exemplo",
      "Host": "",
      "Manifest": "assets/mf.manifest.json",
      "VersionFile": "version.json"
    }
  ]
}
```

`Name` de um serviço é o nome dele no Windows (o nome curto, não o de exibição). `Check` aceita `Tcp` ou `Url`.

## 10. Etapas

Cada etapa é entregue utilizável no ambiente de desenvolvimento.

| Etapa | Entrega | Depende de fora |
|---|---|---|
| 1 | Serviços Windows: descoberta e cadastro, estado, saúde, ações, quedas e reinício automático; área "Processos e serviços" | — |
| 2 | Log ao vivo dos serviços, na tela de trace. **Adiada em 2026-10-08**: nenhum serviço do ambiente grava log em arquivo hoje. Fica especificada (4.2) para quando houver um | — |
| 3 | Frontend: módulos, versões, no ar, integridade, linha do tempo de publicações | — |
| 4 | Tráfego: leitura e rotação do log, agregação, tela com endpoints, instâncias e erros | Formato de log no proxy (5.1) |
| 5 | Uso por módulo e publicações marcadas no tráfego; Visão geral do ambiente; coluna do proxy no mapa. Feito, menos o proxy no mapa, que depende de o agente descobrir qual processo atende em cada porta | 3 e 4 |

## 11. Verificação

- Testes do agente com as partes do Windows atrás de interfaces (gerenciador de serviços, processos, arquivos), como os processos já são; e, para o que só existe no Windows, um roteiro executado no ambiente de desenvolvimento.
- Testes da leitura do log: linhas reais, linha cortada no meio, arquivo rotacionado durante a leitura, normalização de rotas.
- Testes das rotas novas do painel contra um agente de mentira, como as do Worker Control.
- Uso no ambiente de desenvolvimento, com os serviços, o proxy e os frontends de lá.

## 12. Em aberto

1. **Mais de um agente.** O contrato fica preparado; a escolha de agente no painel só é feita quando houver uma segunda máquina.
2. **Outros proxies.** A especificação descreve o NGINX. Outro proxy serve se gravar o mesmo formato de linha.
3. **Versão em uso por usuário** (6.2): depende de mudança no frontend.
