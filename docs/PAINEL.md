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
| Acesso | Login no próprio painel, com usuários criados pelo master; o master inicial é `admin` / `admin` |
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

Quem passa pelo login vê e faz tudo no painel. Não há, por ora, permissão por tela. A única
distinção é o **master**, que é quem cria e remove os outros usuários.

- **Usuários em arquivo próprio.** Ficam em `users.json`, ao lado do executável
  (`ZapMQ:PanelUsersFile`). Na primeira partida, sem nenhum usuário, o master é criado a partir de
  `Panel.User` e `Panel.Password` do `appsettings.json` (valor inicial `admin` / `admin`). Depois
  disso as duas configurações não são mais lidas.
- **Senhas.** Nenhuma senha é guardada: só o resultado de PBKDF2-SHA256 com 210.000 rodadas e um
  sal por usuário. Mínimo de 8 caracteres. Nada disso sai pela API.
- **O master** cria usuários com uma senha inicial, redefine a senha de um usuário, desativa,
  reativa e exclui. Ele próprio não pode ser desativado nem excluído, e a senha dele só é trocada
  por ele, informando a atual.
- **Cada usuário troca a própria senha** no menu com o seu nome, informando a atual. Enquanto a
  senha for a que lhe deram (a inicial do master, ou uma criada ou redefinida pelo master), o
  painel mostra um aviso fixo; no caso do master, o serviço também avisa no log a cada início.
- **Sessão.** Um cookie assinado pelo serviço, válido só para o caminho do painel, com duração
  configurável (padrão: 8 horas). Desativar, excluir ou trocar a senha de um usuário encerra as
  sessões dele na hora. Reiniciar o serviço encerra todas.
- **Tentativas.** Depois de 5 senhas erradas seguidas para o mesmo login, esse login espera um
  minuto, mesmo com a senha certa.
- **Senha do master perdida.** Apagar o `users.json` (ou só a entrada dele) e reiniciar: o master
  volta a ser o das configurações. Os outros usuários precisam ser criados de novo.
- Toda rota de `/api` exige sessão, e as de `/api/users` exigem o master. A verificação está em um
  ponto só, para que a forma de login possa ser trocada depois sem tocar no resto.
- O usuário logado é enviado como `By` nos comandos ao Worker Control e fica no histórico dele.
  Login, criação, exclusão e troca de senha ficam no log do serviço.

Rotas: `POST api/password` (a própria senha); `GET` e `POST api/users`, `PUT api/users/{login}`,
`POST api/users/{login}/password` e `DELETE api/users/{login}` (só o master).

Autenticação do protocolo de mensageria continua fora do escopo.

### 3.1 A administração do Worker Control

O painel pede ao Worker Control, pela fila `WorkerControlAdmin`, tudo o que muda a máquina: parar
processos, trocar arquivos, aplicar scripts, criar serviços. Sem proteção, qualquer programa que
alcance a porta do ZapMQ poderia pedir o mesmo.

- **Proteger.** Em Processos e serviços → Micro serviços, o usuário master usa **Proteger agora**.
  O ZapMQ gera uma chave (256 bits), entrega-a ao Worker Control e os dois a guardam: o ZapMQ em
  `workercontrol.key`, ao lado do arquivo de usuários (`ZapMQ:WorkerControlKeyFile` diz outro
  lugar); o Worker Control em `admin.key`, na pasta de dados dele, cifrada para a máquina.
- **A partir daí** cada pedido vai cifrado e autenticado (AES-256-GCM) com a hora e uma
  identidade: quem apenas alcança o broker não consegue pedir, nem ler o que é pedido, nem
  reenviar um pedido que viu passar. A resposta volta cifrada e amarrada à pergunta. Pedido que
  não venha assim é recusado pelo Worker Control.
- **Trocar a chave**: no menu da mesma tela; a nova vai cifrada com a atual.
- **Desfazer ou recuperar**: apagar `admin.key` na pasta de dados do Worker Control e reiniciá-lo.
  Ele volta a aceitar pedidos sem chave até ser protegido de novo.
- **O que fica de fora**: os dois comandos do protocolo 1.x (`CurrentWorkers` e `ReloadConfig`)
  continuam atendidos sem chave, para o Management Studio antigo. A entrega da primeira chave
  passa pelo broker sem cifra; quem estivesse lendo a fila naquele instante a veria.
- Os relógios das duas máquinas precisam estar a menos de 10 minutos um do outro.

**Enquanto se espera.** Um pedido que demora mais de meio segundo acende uma linha fina sob a
barra do topo e o texto "aguardando o Worker Control…" ou "aguardando o ZapMQ…", conforme quem
está para responder.

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

O ambiente em um desenho, da esquerda para a direita: **Entrada** (a aplicação web e o proxy
reverso), **APIs**, **Micro serviços**, **Serviços e infraestrutura** (os serviços do Windows, os
clientes do ZapMQ que não são de nenhum componente conhecido, o próprio ZapMQ e o Worker Control)
e o **Banco de dados**.

- **Cada componente** mostra o estado (no ar, em parte, fora do ar, desabilitado), uma linha com
  o que importa dele (quantos processos, as portas, o estado do serviço) e, quando usa filas,
  quais consome (↓) e em quantas publica (↑). As filas não são desenhadas: estão dentro de quem as
  usa, na dica ao apontar e no painel ao clicar.
- **O protocolo** com que cada componente fala com o ZapMQ aparece como uma etiqueta: `v2`, ou
  `v1` (em destaque) para quem ainda usa o wrapper 1.x.
- **O banco** aparece em cada componente como um selo: verde e pulsando quando o componente usou
  o banco no último minuto, cinza quando tem conexão aberta e parada, ausente quando não tem
  conexão. Vem de quem está conectado na instância, processo a processo (`sys.dm_exec_sessions`:
  máquina, processo, programa, quantas sessões, quantas em uso, há quanto tempo sem pedir nada).
  Nenhum dado das tabelas é lido. Quem está conectado e não é de nenhum componente (outra máquina,
  uma ferramenta) aparece no painel do banco.
- **As linhas**: HTTP (do proxy para quem recebe, com o ritmo e os erros do período), mensagens
  (de quem publica em uma fila para quem a consome, com o nome das filas) e banco. As que tiveram
  movimento há pouco andam; as de mensagens e de banco podem ser desligadas.
- **Uma API** está no ar quando os sites dela e os pools estão iniciados; sem processo, está só
  ociosa. Parte dos sites parada é aviso; todos, fora do ar.
- **Localizar** procura no nome dos componentes e das filas: diz quem consome e quem publica em
  cada fila encontrada, destaca esses componentes e leva até eles.
- **Zoom**: os botões, ou Ctrl + rolagem; arrastar move. Ao abrir, o desenho cabe na largura;
  "Ver tudo" encaixa o desenho inteiro.
- **O painel ao lado** lista o que pede atenção e, com um componente escolhido, tudo o que se sabe
  dele: o banco, o HTTP, as filas com pendentes e mortas, com quem fala, e o atalho para a tela dele.

O mapa junta o que o broker sabe (aplicações, filas, quem publica e consome), o `Status` do Worker
Control (grupos e serviços), o `Landscape` (APIs com seus sites, aplicações web e conexões do
banco por processo, do Worker Control 2.19) e o tráfego por destino do proxy. Cada aplicação do
broker é atribuída a um componente pelo processo que a abriu.

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

Não há tela própria: quem está conectado aparece no **Mapa** (4.2) e, por fila, em quem a consome e quem publica nela.

### 4.6 Exchanges

Declarar um exchange e as filas que recebem cópia do que é publicado nele. Entra junto com o recurso no servidor (seção 5.4).

### 4.7 Worker Control

Nas listas de aplicações do transporte e de objetos do banco, vários itens podem ser marcados e
levados à expedição de uma vez.

A área **Processos e serviços** tem cinco abas: **Micro serviços**, **Serviços do Windows**,
**Aplicações**, **Proxy reverso** e **Histórico**. Em Micro serviços ficam juntos os dois lados dos
grupos: **Acompanhar** (o que está rodando) e **Configurar** (como cada grupo roda); o cartão de
cada grupo leva direto à configuração dele.

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

### 4.11 Banco de dados

A instância de banco de dados do ambiente, lida pelo Worker Control: no ar ou não, processador,
memória, sessões por aplicação, o que está em execução, bloqueios, bancos, discos, backups, jobs e
as consultas mais caras. Os problemas entram em "Atenção" na Visão geral. Nenhum dado de tabela
chega ao painel. Especificação em [BANCO.md](BANCO.md).

### 4.8 Trace

Acompanhar ao vivo o `Trace()` de um worker, a partir do menu do processo na tela do Worker Control. O contrato com os workers está na [especificação do Worker Control](https://github.com/MurilloLazzaretti/Worker-Control/blob/main/docs/ESPECIFICACAO-2.0.md), seção 11.3.

- **De um processo ou do grupo inteiro.** No menu de um processo, o trace dele; no rodapé do cartão do grupo, o de todos os processos do grupo juntos, na ordem em que as linhas foram escritas, cada processo com uma cor. É o que serve quando não se sabe qual instância vai pegar a mensagem. Um processo substituído ou acrescentado entra na tela sozinho, e as linhas do que saiu ficam. Cada processo pode ser escondido e mostrado de novo.
- **Só enquanto alguém assiste.** Abrir a tela é o que liga o trace no processo; o serviço renova o pedido a cada 10 segundos enquanto houver alguém assistindo, e ao sair o último o trace é desligado. Se o painel ou o ZapMQ caírem, o processo desliga sozinho em 30 segundos.
- **Descartável.** As linhas trafegam em filas `zapmq.trace.<pid>`, que o broker trata como descartáveis: não geram mensagens mortas, e o que ninguém consome some em 15 segundos. Um navegador que não acompanha o volume perde as linhas mais antigas que ainda não leu.
- **Histórico curto.** O serviço guarda as últimas 2.000 linhas de cada processo, por até 5 minutos depois de o último sair; quem chega depois as vê.
- **Na tela:** filtro por texto, pausa (as linhas continuam chegando e são contadas), quebra de linha, limpar e salvar em arquivo. Quando o processo descarta linhas por excesso de volume, a tela diz quantas.
- **Serviços Windows:** o cartão de um serviço acompanhado tem o item Trace, que segue o processo do serviço e os que ele iniciou. Só há o que ver quando o serviço usa o wrapper de worker; sem ele, a tela diz que o processo não atende pedidos de trace, e nada é enviado a ele.
- **Processos com o trace antigo** (Delphi, ou wrapper .NET anterior ao 2.0): o serviço pede ao Worker Control, que abre o socket na máquina do processo e publica o que chega. A tela avisa que o trace está sendo repassado. Sem o Worker Control 2.1 no ar, a tela explica por que não há trace.

### 4.12 Configurações

Os parâmetros do ambiente em uma tela, por assunto: **Banco de dados** (a instância acompanhada,
os bancos, o usuário e a senha, guardada cifrada para a máquina, e os limites dos alertas),
**Aplicações** (as pastas em que as aplicações novas são criadas, o nome dos sites, a espera para
parar, as cópias guardadas e os arquivos do ambiente), **Aplicação web** (cada aplicação, com a
arquitetura: **MFE**, casca e módulos trocados separados, ou **monolito**, trocado inteiro),
**Tráfego HTTP**, **Proxy reverso** e **Worker Control**. Tudo fica no `Environment.json` do Worker Control,
separado do `ConfigWorkers.json`, que guarda só o que é dele (onde está o ZapMQ, os grupos, os serviços
acompanhados) e é editado em Micro serviços → Configurar. O Worker Control confere antes de passar a
usar; uma recusa aparece com o motivo. Ao abrir a tela, o que está em uso sem estar escrito (os padrões e
o que foi deduzido da máquina: pastas, nome dos sites, proxy) vem preenchido, para ser gravado. O que é do próprio
ZapMQ (portas, nome do ambiente, retenções, log) é editado no assunto **ZapMQ**, só pelo master, e
fica em `zapmq.settings.json`, ao lado do executável (`ZapMQ:SettingsFile` diz outro lugar): ele é
lido por cima do `appsettings.json` que vem com o programa, que nunca é escrito, e passa a valer
quando o serviço é reiniciado. A tela mostra o que está gravado e o que está em uso até lá.

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
5. **Usuários em arquivo próprio, com a senha protegida**; a do `appsettings.json` só cria o master na primeira partida (seção 3).
