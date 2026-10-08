# ZapMQ — Painel de administração

Situação: aprovada em 2026-10-07. Etapas A, B e C e o trace implementados; falta a etapa D (exchange).
Última revisão: 2026-10-08.

Este documento especifica o painel web do ZapMQ (versão 2.2), que inclui a seção do Worker Control. Corresponde à fase 6 do [plano](PLANO-2.0.md) e à etapa 4 da [especificação do Worker Control 2.0](https://github.com/MurilloLazzaretti/Worker-Control/blob/main/docs/ESPECIFICACAO-2.0.md).

---

## 1. O que o painel é

Uma aplicação web, servida pelo próprio serviço do ZapMQ, para acompanhar e administrar o broker e tudo o que conversa por ele: filas, mensagens, aplicações conectadas e os processos mantidos pelo Worker Control.

Decisões já tomadas:

| Assunto | Decisão |
|---|---|
| Interface | Angular |
| Porta | 5680, própria, separada da mensageria (5679) |
| Publicação | Atrás do proxy reverso existente, no caminho `/zapmq` |
| Acesso | Login no próprio painel, com um usuário e senha configuráveis; valor inicial `admin` / `admin` |
| Vínculo com outros sistemas | Nenhum: o painel é parte do ZapMQ e não depende de nenhuma aplicação que use o broker |

## 2. Arquitetura

```
navegador ──► proxy reverso (/zapmq) ──► serviço ZapMQ, porta 5680
                                          ├─ arquivos da interface (Angular, dentro do executável)
                                          ├─ /api/...      consulta e administração
                                          └─ /api/live     atualizações ao vivo
                                                 │
                              núcleo do broker ──┴── fila WorkerControlAdmin ──► Worker Control
```

- **Um executável.** A interface é compilada no momento do publish e embutida no `ZapMQ.exe`. O Node.js é necessário só na máquina que compila; o servidor continua sem nada instalado.
- **Caminho base configurável.** O painel funciona em `/` (acesso direto à porta) e em `/zapmq` (atrás do proxy), sem recompilar.
- **Atualizações ao vivo** por uma conexão permanente do navegador (Server-Sent Events). O proxy precisa repassá-la sem acumular a resposta; a configuração de exemplo vai no `README.md`.
- **Worker Control.** O painel não fala com o serviço do Worker Control diretamente: usa a fila `WorkerControlAdmin`, pelo contrato de administração 2.0. O Worker Control pode estar em outra máquina. Sem ele no ar, a seção correspondente diz isso e o resto do painel funciona.
- **As rotas `/admin` saem da porta da mensageria** e passam a existir só na porta do painel, atrás do login, como decidido na especificação do protocolo v2.

Bibliotecas da interface, sempre na versão estável mais recente: Angular (22 na etapa A), Angular Material para os componentes e ECharts para gráficos e para o mapa. Fontes e ícones também vão embutidos. A interface se adapta do celular ao monitor largo e tem tema claro e escuro. Nenhuma delas é carregada da internet; tudo vai dentro do executável.

## 3. Acesso

- Tela de login. Usuário e senha ficam no `appsettings.json` (`Panel.User`, `Panel.Password`), com o valor inicial `admin` / `admin`.
- Enquanto a senha for a inicial, o serviço registra um aviso no log a cada início e o painel mostra um aviso fixo.
- A sessão é um cookie assinado pelo serviço, válido só para o caminho do painel, com duração configurável (padrão: 8 horas).
- Toda rota de `/api` exige sessão. A verificação está em um ponto só, para que a forma de login possa ser trocada depois sem tocar no resto.
- O usuário logado é enviado como `By` nos comandos ao Worker Control e fica no histórico dele.

Autenticação do protocolo de mensageria continua fora do escopo.

## 4. Telas

### 4.1 Visão geral

Indicadores do momento e gráficos do período recente.

| Indicador | Origem |
|---|---|
| Mensagens publicadas, entregues e confirmadas por segundo | ZapMQ |
| Mensagens pendentes e em processamento, no total | ZapMQ |
| Mensagens mortas, por motivo | ZapMQ |
| Aplicações conectadas (v2) e clientes v1 ativos | ZapMQ |
| Workers no ar sobre o total desejado; grupos instáveis | Worker Control |
| Filas mais carregadas e filas sem consumidor | ZapMQ |
| Últimos eventos (quedas, travamentos, mensagens não confirmadas) | ZapMQ e Worker Control |

Gráficos: vazão e pendentes na última hora e nas últimas 24 horas. Para isso o serviço passa a guardar em memória uma amostra a cada 5 segundos (última hora) e uma por minuto (24 horas). Com a persistência (2.3) esse histórico sobrevive ao reinício.

### 4.2 Mapa

O parque em um desenho: quem publica em qual fila, quem consome, e em que estado cada parte está.

- **Nós:** aplicações (uma por nome de executável, com o número de instâncias), filas, exchanges e o próprio Worker Control.
- **Ligações:** aplicação → fila (publica) e fila → aplicação (consome), com a vazão recente na espessura da linha.
- **Estado nas cores:** aplicação com instâncias faltando ou grupo instável; fila acumulando; fila com mensagens mortas recentes; fila sem consumidor.
- **Detalhe ao clicar:** abre a tela da fila, do grupo ou da aplicação.

De onde vêm os dados:

| Informação | Como o servidor sabe |
|---|---|
| Aplicação conectada, máquina, processo | Saudação do protocolo v2 |
| Filas que ela consome | Vínculos da conexão v2 |
| Filas em que ela publica | O servidor passa a registrar, por conexão v2, em quais filas ela publicou recentemente |
| A que grupo do Worker Control ela pertence | Cruzamento do número do processo com o estado do Worker Control |
| Clientes v1 (Delphi, DLL antiga) | Só o endereço de rede e as filas que consultam ou em que publicam. Aparecem como "cliente v1" por endereço; um worker Delphi conhecido do Worker Control aparece pelo grupo dele |

Filas de uso interno (keep-alive, safe stop e trace de cada processo) são recolhidas dentro do nó da aplicação, para o desenho mostrar as filas de trabalho.

Como ficou: o desenho é em colunas (quem publica, filas, quem consome, supervisão), feito com HTML e SVG próprios em vez do ECharts, que continua nos gráficos; assim os nós são botões comuns, com o tema e a acessibilidade do resto do painel. A vazão de cada ligação é medida pelo navegador entre uma leitura e outra (janela de um minuto). O servidor lembra quem publicou em cada fila por até 24 horas; a tela escolhe olhar 15 minutos, 1 hora ou 24 horas. Ao clicar, abre-se um resumo com as ligações do nó e o atalho para a tela dele. Uma fila é dita "acumulando" quando passa 30 segundos com mensagens esperando sem diminuir.

Com muitas filas, o desenho se resume de duas formas, as duas na barra do mapa:

- **Agrupar filas iguais** (ligado por padrão): filas que têm exatamente os mesmos publicadores e os mesmos consumidores são desenhadas como uma só, uma pilha com a quantidade, a partir de três. Clicar na pilha lista as filas e permite mostrá-las separadas. Uma fila com algo errado nunca entra em pilha: aparece sozinha, com a cor do problema.
- **Só filas com movimento:** deixa de fora as filas em que nada foi publicado no período escolhido e que não têm nada pendente nem problema. A barra diz quantas ficaram ocultas.

Há também um campo para localizar uma aplicação ou uma fila pelo nome, inclusive uma fila que está dentro de uma pilha. As colunas das pontas (o proxy e o Worker Control) são mais estreitas, e um nome com pontos que não cabe perde o começo, não o fim, que é o que distingue um do outro.

### 4.3 Filas

Lista com pendentes, em processamento, consumidores, vazão e mensagens mortas, com busca e ordenação. Na fila:

- **Definição:** retenção, reentrega de mensagem não confirmada, limites das mensagens mortas. Editável com o serviço rodando e gravada em arquivo do serviço (seção 5.2). Criar a definição de uma fila que ainda não existe também é aqui.
- **Mensagens pendentes:** lista e conteúdo, só para leitura.
- **Consumidores e publicadores** recentes.
- **Ações:** pausar e retomar a entrega; esvaziar a fila. As duas pedem confirmação.

### 4.4 Mensagens mortas

Por fila: lista com motivo, horários e quem tinha recebido; conteúdo; reenviar para a fila de origem; descartar uma ou todas.

Reenviar pede confirmação e lembra a regra do ambiente: a mensagem pode já ter sido processada, total ou parcialmente.

### 4.5 Aplicações conectadas

As conexões v2: aplicação, processo, máquina, versão do wrapper, desde quando, filas vinculadas, se está processando algo. Os clientes v1 ativos, por endereço.

### 4.6 Exchanges

Declarar um exchange e as filas que recebem cópia do que é publicado nele. Entra junto com o recurso no servidor (seção 5.4).

### 4.7 Worker Control

- **Grupos e workers ao vivo:** estado, quantidade desejada e por quê (base, boost, escala pela fila), reciclagem em andamento, grupo instável.
- **Por worker:** tempo no ar, processador, memória e tempo de resposta do keep-alive, com gráfico das últimas horas.
- **Ações:** habilitar e desabilitar grupo, mudar a quantidade, reiniciar um worker ou um grupo.
- **Configuração:** formulário por grupo (incluindo janelas de boost, escala pela fila e reciclagem) e edição do arquivo inteiro, validada antes de gravar.
- **Histórico de eventos**, com filtro por grupo e tipo, dos mais recentes para os mais antigos.
- **Serviço:** parar deixando os workers rodando. Iniciar o serviço do Windows pelo painel não foi implementado: é feito na máquina do Worker Control.

### 4.9 Mensagens: observar e publicar

- **Observar** (na tela da fila): as mensagens que passam por ela, ao vivo, com quem publicou, quem recebeu, o estado (esperando, entregue, confirmada, respondida, morta), o tempo até a confirmação e o conteúdo; em RPC, a resposta também. Olhar não tira, não segura e não atrasa nenhuma mensagem. A fila só conta o que passa por ela enquanto alguém observa; o conteúdo não fica guardado em lugar nenhum.
- **Guardar as últimas mensagens** é uma opção da definição da fila (`KeepRecent`, até 500), desligada por padrão. Ligada, quem abre o Observar vê também o que passou antes de chegar. Fica em memória: não sobrevive ao reinício do serviço.
- Conteúdo ou resposta com mais de 256 KB é cortado na captura, e a tela diz.
- **Publicar** (na tela da fila e no Observar): uma mensagem escrita à mão, com editor de JSON (validação, formatação), abrir e salvar arquivo, validade e RPC. Em RPC a tela espera a resposta e a mostra. Antes de publicar, a tela lembra que a mensagem será processada de verdade. Quem publicou fica no log do serviço e aparece como "painel (usuário)" entre os publicadores da fila.
- **Modelos:** corpos salvos com nome, por fila, para publicar de novo sem redigitar. Ficam em `models.json`, ao lado do executável, e valem para todos que usam o painel.
- **Editar e publicar:** em qualquer mensagem aberta (pendente, morta ou observada), leva o conteúdo para o editor.

### 4.10 Clientes do protocolo 1.x

O protocolo 1.x não diz quem é o cliente. O servidor passa a reconhecê-lo de outra forma quando ele está na mesma máquina: pergunta ao sistema qual processo é dono da conexão, e com isso o cliente ganha nome e número de processo. Aparece assim nas conexões, entre os publicadores e consumidores de cada fila, no Observar e no mapa, onde passa a ser uma aplicação como as outras — ligada às filas em que publica e de que consome, e reconhecida como grupo do Worker Control ou como serviço Windows pelo número do processo. De outra máquina, o cliente continua sendo só o endereço.

Um cliente que roda dentro do processo de trabalho do IIS (`w3wp`) é conhecido pela aplicação que esse processo hospeda — a maior biblioteca que ele carregou de fora do sistema — e não por `w3wp`, que é o mesmo executável para todas.

Um consumidor 1.x escuta vindo perguntar. A partir de agora a fila passa a existir quando alguém pergunta por ela, com quem perguntou anotado, mesmo vazia: é o que faz as filas consumidas por clientes 1.x aparecerem nas telas e no mapa sem esperar uma mensagem passar. Uma fila definida no painel também é desenhada no mapa mesmo parada.

### 4.8 Trace

Acompanhar ao vivo o `Trace()` de um worker, a partir do menu do processo na tela do Worker Control. O contrato com os workers está na [especificação do Worker Control](https://github.com/MurilloLazzaretti/Worker-Control/blob/main/docs/ESPECIFICACAO-2.0.md), seção 11.3.

- **De um processo ou do grupo inteiro.** No menu de um processo, o trace dele; no rodapé do cartão do grupo, o de todos os processos do grupo juntos, na ordem em que as linhas foram escritas, cada processo com uma cor. É o que serve quando não se sabe qual instância vai pegar a mensagem. Um processo substituído ou acrescentado entra na tela sozinho, e as linhas do que saiu ficam. Cada processo pode ser escondido e mostrado de novo.
- **Só enquanto alguém assiste.** Abrir a tela é o que liga o trace no processo; o serviço renova o pedido a cada 10 segundos enquanto houver alguém assistindo, e ao sair o último o trace é desligado. Se o painel ou o ZapMQ caírem, o processo desliga sozinho em 30 segundos.
- **Descartável.** As linhas trafegam em filas `zapmq.trace.<pid>`, que o broker trata como descartáveis: não geram mensagens mortas, e o que ninguém consome some em 15 segundos. Um navegador que não acompanha o volume perde as linhas mais antigas que ainda não leu.
- **Histórico curto.** O serviço guarda as últimas 2.000 linhas de cada processo, por até 5 minutos depois de o último sair; quem chega depois as vê.
- **Na tela:** filtro por texto, pausa (as linhas continuam chegando e são contadas), quebra de linha, limpar e salvar em arquivo. Quando o processo descarta linhas por excesso de volume, a tela diz quantas.
- **Serviços Windows:** o cartão de um serviço acompanhado tem o item Trace, que segue o processo do serviço e os que ele iniciou. Só há o que ver quando o serviço usa o wrapper de worker; sem ele, a tela diz que o processo não atende pedidos de trace, e nada é enviado a ele.
- **Processos com o trace antigo** (Delphi, ou wrapper .NET anterior ao 2.0): o serviço pede ao Worker Control, que abre o socket na máquina do processo e publica o que chega. A tela avisa que o trace está sendo repassado. Sem o Worker Control 2.1 no ar, a tela explica por que não há trace.

## 5. O que o servidor ganha

### 5.1 API do painel

Rotas em `/api`, todas atrás do login: visão geral e séries dos gráficos, mapa, filas (lista, detalhe, definição, mensagens pendentes, pausar, esvaziar), mensagens mortas, conexões, exchanges e as rotas que repassam comandos ao Worker Control.

### 5.2 Definições de fila gravadas

As definições feitas pelo painel vão para `queues.json`, ao lado do executável, gravado de forma atômica. Na subida valem as do `appsettings.json` e, por cima delas, as do `queues.json`.

### 5.3 Recursos novos no núcleo

| Recurso | Para quê |
|---|---|
| Ler as mensagens pendentes sem consumi-las | Tela da fila |
| Esvaziar uma fila | Ação da tela da fila. As mensagens removidas não vão para as mensagens mortas; a ação fica no log |
| Pausar e retomar a entrega de uma fila | Ação da tela da fila. Pausada, a fila continua recebendo e não entrega a ninguém, v1 ou v2 |
| Registro de quem publicou em cada fila | Mapa |
| Séries de vazão e pendentes | Gráficos |

### 5.4 Exchange

Como no plano: um nome declarado como exchange distribui uma cópia da mensagem para cada fila ligada a ele. Quem publica usa o envio comum com o nome do exchange; quem consome, as filas comuns. Vale para v1 e v2.

## 6. Configuração

Chaves novas no `appsettings.json`, todas opcionais:

```json
{
  "ZapMQ": {
    "Panel": {
      "Enabled": true,
      "Port": 5680,
      "BasePath": "/zapmq",
      "User": "admin",
      "Password": "admin",
      "SessionHours": 8
    }
  }
}
```

`BasePath` vazio serve o painel na raiz da porta.

## 7. Etapas

O painel é grande; cada etapa abaixo é entregue utilizável no ambiente de desenvolvimento.

| Etapa | Entrega |
|---|---|
| A | Estrutura (Angular embutido no serviço, porta, caminho base, login) e as telas do broker: visão geral, filas com definição editável e gravada, mensagens mortas, aplicações conectadas |
| B | Seção do Worker Control completa |
| C | Mapa |
| D | Exchange, no servidor e no painel |

O trace vem depois, com a etapa própria.

## 8. Verificação

- Testes das rotas da API e dos recursos novos do núcleo, como os atuais.
- Testes da interface nos pontos com lógica (formulários de definição e de configuração, montagem do mapa).
- Um roteiro automatizado em navegador contra o serviço real: login, cada tela, uma ação de cada tipo.
- Uso no ambiente de desenvolvimento, atrás do proxy em `/zapmq`.

## 9. Decisões tomadas

1. **Bibliotecas da interface:** Angular Material e ECharts (seção 2).
2. **Ordem das etapas** (seção 7): telas do broker, depois Worker Control, depois mapa, depois exchange.
3. **Esvaziar fila não gera mensagens mortas** (seção 5.3): as mensagens somem, com registro no log.
4. **Pausar fila vale para v1 e v2** (seção 5.3).
5. **Senha no `appsettings.json` em texto**, por ora, com aviso enquanto for a inicial (seção 3).
