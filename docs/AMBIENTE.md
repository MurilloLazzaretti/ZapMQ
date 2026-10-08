# ZapMQ — Monitoramento do ambiente

Situação: aprovada em 2026-10-08. Etapa 1 implementada.
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
| Saúde | Processador, memória, threads e handles do processo, com histórico e gráfico | Diz se está vivo e quanto consome, não se está travado por dentro |
| Verificação | Opcional, por serviço: uma porta TCP que precisa aceitar conexão, ou uma URL que precisa responder 2xx | Só o que o serviço expõe |
| Iniciar, parar, reiniciar | Pelo gerenciador de serviços, com confirmação; quem fez fica no histórico | Parar espera até `StopTimeoutMs`; depois disso informa que não parou, e não encerra à força. Parar um serviço para também os que dependem dele, como faz o Windows |
| Queda | Evento no histórico quando o serviço para sem ter sido pedido. É queda quando o Windows registra um código de saída diferente de zero; parada limpa feita por fora do painel é registrada como parada | Um serviço que cai devolvendo código zero é visto como parada limpa |
| Reinício automático | Opcional (`AutoRestart`), desligado por padrão. Ligado, o agente inicia de novo o serviço que parou sozinho, com o mesmo recuo crescente dos grupos quando cai em sequência | Não age sobre serviço parado por alguém |
| Atividade no ZapMQ | Não se informa no cadastro: o serviço que usa o broker pelo protocolo v2 é reconhecido pelo número do processo, e aparece no mapa com as filas e a vazão (etapa 5) | Só os que usam o broker; os que usam o protocolo 1.x são vistos por endereço, sem ligação com o serviço |
| Log ao vivo | Na tela de trace: o agente acompanha o arquivo de log do serviço e publica as linhas novas | Só serviço que grava log em arquivo |

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

- **Rota normalizada.** A query string é descartada, e os trechos do caminho que são identificadores viram `{id}`: números, GUIDs, e textos longos em hexadecimal ou base64. `/api/pedidos/48213/itens?x=1` é contado como `/api/pedidos/{id}/itens`. Regras adicionais por configuração (`Traffic.Routes`), para os casos que a regra geral não pega.
- **Agregado por minuto**, por rota, método, servidor virtual e instância do upstream: quantidade, por classe de status (2xx, 3xx, 4xx, 5xx), bytes, e a distribuição dos tempos em faixas fixas (o bastante para mediana, p95 e p99). Retenção: `Traffic.RetentionDays` (padrão 30). Depois de 48 horas, os minutos são consolidados em horas.
- **Limite de rotas.** No máximo `Traffic.MaxRoutes` rotas distintas por dia (padrão 2.000); o excedente é somado em "outras". Protege contra caminho aleatório vindo de varredura.
- **Endereços.** Por rota e por hora, o conjunto de endereços distintos, para contar usuários. Retenção: a mesma do tráfego.
- **Erros recentes.** As últimas `Traffic.KeepErrors` requisições com 5xx (padrão 500), com horário, rota original sem query string, status, instância e tempo, para ver o que falhou sem abrir o arquivo.

### 5.3 O que a tela mostra

- **Agora:** requisições por segundo, taxa de erro (4xx e 5xx separados), mediana e p95, no total.
- **Por aplicação** (o primeiro trecho do caminho que o proxy usa para rotear, ou o servidor virtual): os mesmos números, com gráfico da última hora e do último dia.
- **Endpoints:** tabela com busca e ordenação — mais chamados, mais lentos, com mais erro, e os que pioraram em relação ao mesmo horário do dia anterior. O detalhe de um endpoint mostra a série dele, a divisão por status e por instância.
- **Instâncias:** para cada upstream com mais de uma, quanto cada uma recebeu, o tempo e os erros de cada uma. Uma instância que não recebe nada, ou que responde pior que a outra, aparece.
- **Erros recentes** (5.2).
- **Períodos:** última hora, 24 horas, 7 dias, 30 dias.

Tráfego de arquivos estáticos (o frontend) é mostrado separado do das APIs, para não dominar os números.

## 6. Micro frontends

### 6.1 De onde vêm os dados

De arquivos, na pasta em que o proxy serve o frontend (`Frontends[].Root`):

- o **manifesto** dos módulos (`Manifest`, por exemplo `assets/mf.manifest.json`): um objeto `nome → caminho do ponto de entrada`;
- em cada módulo, o **arquivo de versão** (`VersionFile`, por exemplo `version.json`). Dele o agente lê, quando existirem, `nome`, `build` e a lista `versions` com `version`, `date` e `descriptions`; a versão atual é a primeira da lista. Outros formatos podem ser mapeados depois; sem arquivo de versão, o módulo aparece com a data de publicação apenas.

### 6.2 O que se vê

Por módulo:

| Informação | Como |
|---|---|
| Versão, build, data e o que mudou | Arquivo de versão |
| Publicado em | Data do ponto de entrada no disco |
| No ar | O agente pede o ponto de entrada pelo proxy (`Frontends[].BaseUrl`) e espera 200. Verificado a cada minuto |
| Íntegro | O manifesto lista o módulo e a pasta dele existe, com o ponto de entrada; pasta sem entrada no manifesto também é apontada |
| Uso | Requisições e endereços distintos do módulo, do tráfego (seção 5) |
| Arquivos pedidos que não existem | 404 em arquivos do módulo: costuma ser navegador com versão antiga em cache |

E, para o conjunto:

- **Linha do tempo de publicações.** O agente percebe quando o ponto de entrada ou o arquivo de versão de um módulo muda e registra data, versão e build. Guardado como evento, no histórico.
- **Publicação marcada no tráfego.** Os gráficos da tela de tráfego mostram o instante de cada publicação; erro que sobe depois de publicar aparece ao lado da causa.

Saber qual versão cada usuário tem aberta exige que o frontend informe, o que é mudança nele. Fica fora desta especificação.

## 7. Telas

| Hoje | Passa a ser |
|---|---|
| Visão geral (só o broker) | **Visão geral** do ambiente: um bloco para mensageria, um para processos e serviços, um para tráfego, um para frontends; cada um com dois ou três números, o que pede atenção, e o caminho para a tela própria |
| Mapa | **Mapa**, com uma coluna a mais, à esquerda: o proxy e as aplicações que ele alcança. Os serviços Windows que usam o broker entram como aplicações, com o estado do serviço |
| Filas, Mensagens mortas | Sem mudança |
| Aplicações, Worker Control | **Processos e serviços**: grupos e workers, serviços Windows e conexões, em abas. Histórico e Configuração continuam nela, e passam a cobrir os serviços |
| — | **Tráfego** |
| — | **Frontend** |

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
    "Routes": []
  },
  "Frontends": [
    {
      "Name": "Aplicação",
      "Root": "D:\\www\\app",
      "BaseUrl": "http://app.exemplo",
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
| 2 | Log ao vivo dos serviços, na tela de trace | — |
| 3 | Frontend: módulos, versões, no ar, integridade, linha do tempo de publicações | — |
| 4 | Tráfego: leitura e rotação do log, agregação, tela com endpoints, instâncias e erros | Formato de log no proxy (5.1) |
| 5 | Uso por módulo e publicações marcadas no tráfego; Visão geral do ambiente; coluna do proxy no mapa | 3 e 4 |

## 11. Verificação

- Testes do agente com as partes do Windows atrás de interfaces (gerenciador de serviços, processos, arquivos), como os processos já são; e, para o que só existe no Windows, um roteiro executado no ambiente de desenvolvimento.
- Testes da leitura do log: linhas reais, linha cortada no meio, arquivo rotacionado durante a leitura, normalização de rotas.
- Testes das rotas novas do painel contra um agente de mentira, como as do Worker Control.
- Uso no ambiente de desenvolvimento, com os serviços, o proxy e os frontends de lá.

## 12. Em aberto

1. **Mais de um agente.** O contrato fica preparado; a escolha de agente no painel só é feita quando houver uma segunda máquina.
2. **Outros proxies.** A especificação descreve o NGINX. Outro proxy serve se gravar o mesmo formato de linha.
3. **Versão em uso por usuário** (6.2): depende de mudança no frontend.
