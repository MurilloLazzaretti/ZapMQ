# Transporte entre ambientes

Especificação do transporte e da aprovação de mudanças entre ambientes (por exemplo DEV → QAS →
PRD) pelo painel do ZapMQ. Complementa [AMBIENTE.md](AMBIENTE.md) e [BANCO.md](BANCO.md).

**O que já existe** (ZapMQ 2.2.22, Worker Control 2.11): as etapas 1 e 2 da seção 9, isto é,
itens de banco e de arquivos (micro serviços, serviços do Windows, APIs e módulos web) de ponta a
ponta, para alvos que já existem no destino, com o pacote levado de um ambiente ao outro como
arquivo. O resto deste documento é o que foi combinado e ainda falta fazer; a seção 11 diz como o
que existe funciona.

## 1. Regras

- **Uma mudança viaja inteira, em um pacote.** O pacote que foi aplicado em um ambiente é o mesmo
  que segue para o próximo, sem ser remontado.
- **Nada é aplicado sem aprovação no destino.** Chegar não é aplicar: o pacote fica pendente até
  alguém do ambiente de destino aprovar.
- **Aplicar é um ato explícito, feito uma única vez.** Um pacote recebido duas vezes é recusado na
  segunda; aplicar de novo um pacote já aplicado é uma decisão de alguém, registrada como tal.
- **O ciclo de um envio termina na entrega.** Quem envia fica sabendo que o destino recebeu. O
  resultado da aprovação e da aplicação pertence ao destino e fica lá; não volta à origem.
- **Configuração não viaja.** Os arquivos próprios de cada ambiente nunca entram em um pacote nem
  são sobrescritos por um.
- **Quem faz o quê.** O painel (ZapMQ) guarda os pacotes, as aprovações e o histórico, e leva o
  pacote de um ambiente ao outro. O agente (Worker Control) é quem toca na máquina: lê o que vai
  para o pacote e aplica o que foi aprovado, inclusive parando e iniciando o que for preciso.
- **Qualquer usuário do painel monta, envia, aprova e aplica**, por ora. Tudo fica registrado com
  o usuário e a hora.

## 2. O que pode ser transportado

| Tipo | Item | O que viaja |
|---|---|---|
| `database` | procedure, function, view, type, ou um script livre | o script |
| `worker` | um grupo de processos do Worker Control (micro serviço) | a pasta publicada |
| `service` | um serviço do Windows | a pasta publicada |
| `api` | uma aplicação atrás do proxy, hospedada no servidor web | a pasta publicada |
| `frontend` | um módulo da aplicação web, ou a aplicação que os carrega | a pasta do módulo |

**Banco.** O programador desenvolve direto no banco de DEV. O item nasce escolhendo o objeto no
próprio banco, e o agente captura o script atual; o `.sql` é só a forma em que ele viaja dentro
do pacote. Importar ou escrever um `.sql` em DEV também é possível, e é o caminho para o que não
é um objeto.

- **Tabela nova** viaja como o `CREATE` que o agente monta.
- **Tabela que já existe no destino e mudou** não tem alteração automática: calcular a mudança a
  partir de duas definições pode perder dados. O painel mostra a diferença e alguém escreve o
  script de alteração, que viaja como script livre.
- **Objeto apagado** viaja como um `DROP`, sempre como item explícito, destacado na aprovação.
- **Criar um banco de dados não faz parte do transporte.**

**Coisas novas.** Um pacote também pode levar o que ainda não existe no destino: uma API, um
micro serviço, um serviço do Windows ou um módulo novo. Para objetos de banco isso é natural (o
`CREATE`). Para os tipos de arquivos é a seção 4.1.

## 3. O pacote

Um arquivo só (`.zpkg`, um zip), com o mesmo formato para todos os tipos:

```
package.json            o manifesto
items/01/script.sql     um item de banco
items/01/undo.sql       opcional: como desfazer
items/02/files.zip      um item de arquivos: a pasta publicada, já sem a configuração
```

Manifesto:

| Campo | Para quê |
|---|---|
| `id` | Identidade do pacote, a mesma em todos os ambientes. É o que impede receber duas vezes |
| `name`, `description` | O que a mudança é, nas palavras de quem montou |
| `createdAt`, `createdBy`, `origin` | Quando, quem e em qual ambiente nasceu |
| `trail` | Por onde passou: ambiente, quem enviou, quem aprovou, quando foi aplicado |
| `items[]` | Os itens, na ordem em que são aplicados |

Cada item:

| Campo | Para quê |
|---|---|
| `kind` | `database`, `worker`, `service`, `api`, `frontend` |
| `name` | O nome lógico do alvo, igual em todos os ambientes (seção 4) |
| `action` | `create`, `alter`, `drop`, `script` (banco); `replace` (arquivos) |
| `fingerprint` | A impressão digital do conteúdo que o item leva |
| `base` | A impressão digital que o alvo tinha na origem quando o item foi montado |
| `version` | Para arquivos: a versão que o executável ou o módulo declara |
| `sha256`, `size` | Do conteúdo, conferidos na chegada |

**Formato normalizado por tipo.** Um item de arquivos é sempre o zip da pasta publicada, com os
arquivos na raiz, sem a pasta de fora. Na montagem saem os arquivos de configuração (padrão:
`appsettings*.json`, `web.config`, `ConfigWorkers.json`, `*.db`, `logs/`; configurável por alvo).
Um item de banco é sempre um arquivo `.sql` em UTF-8, com os lotes separados por `GO`.

## 4. Alvos e nomes lógicos

O mesmo item tem endereços diferentes em cada ambiente: o banco se chama de um jeito em DEV e de
outro em QAS; uma API fica em outra pasta. O pacote carrega só o **nome lógico**; cada agente sabe
para onde ele aponta na sua máquina.

- **Banco:** cada banco de `Databases` ganha um apelido (`Alias`). O pacote diz "banco
  `principal`"; em cada ambiente o agente traduz para o nome real.
- **Micro serviço e serviço do Windows:** o nome do grupo e o nome do serviço, que já estão na
  configuração do agente. A pasta é a do executável.
- **API:** o nome da aplicação como o tráfego já a conhece (`api/cadastro`). A pasta vem dos sites
  do servidor web, que o agente já lê. Uma aplicação com mais de uma instância tem todas as
  pastas trocadas.
- **Frontend:** o nome do módulo, sob a raiz de `Frontends`.

O que não puder ser deduzido é declarado em uma seção `Transport.Targets` da configuração do
agente.

**O banco, enquanto houver um só.** Quem sabe onde aplicar é o destino, não a origem. Com um único
banco em `Databases`, o pacote não nomeia banco nenhum: diz "o banco do ambiente" e cada destino
aplica no seu. Só quando um ambiente acompanhar mais de um banco é que os itens passam a levar um
nome, que precisa ser o mesmo nos ambientes (um apelido, traduzido por cada agente).

### 4.1 Alvo que ainda não existe

Um item de arquivos cujo alvo não existe no destino é uma **instalação**, e a aprovação muda:

- o pacote leva, além dos arquivos, o que o alvo é: o executável, o tipo e, quando fizer sentido,
  um **modelo** dos arquivos de configuração, sem os valores do ambiente de origem;
- quem aprova informa o que é próprio do ambiente: a pasta, as instâncias e as portas, o número
  de processos do grupo, o nome e a conta do serviço, e preenche a configuração a partir do
  modelo;
- o agente cria a pasta, copia os arquivos e registra o alvo: o grupo no `ConfigWorkers.json`, o
  serviço no Windows, o site no servidor web, o módulo no manifesto da aplicação web.

O que envolve o proxy reverso (uma rota nova para uma API nova) fica fora da instalação
automática: o painel mostra o trecho a incluir, e a mudança no proxy é feita por alguém.

## 5. O caminho de uma mudança

### 5.1 Entrada

De duas formas, que dão no mesmo lugar, a **área** do ambiente:

- **Pelo painel:** escolher objetos do banco, nas abas Objetos e Alterações (esta marca o que
  mudou e ainda não entrou em nenhum pacote); escrever ou importar um script; ou enviar o zip de
  uma pasta publicada dizendo o tipo e o alvo.
- **Por pasta:** uma pasta de entrada observada pelo agente, com uma subpasta por tipo
  (`banco/`, `worker/`, `service/`, `api/`, `frontend/`). O nome do arquivo é o nome do alvo:
  `api/cadastro.zip`, `banco/ajuste-pedido.sql`.

Em DEV também se pode montar um item **a partir do que está rodando**: o agente empacota a pasta
atual do alvo.

### 5.2 Montagem

Na área, escolhem-se os itens, a ordem e a descrição, e o pacote é fechado. O script de cada
objeto é capturado nesse momento, não quando ele entrou na área. A ordem é calculada pelas
dependências (types e tabelas antes de functions, views e procedures) e pode ser ajustada à mão.
Fechado, o pacote não muda mais: uma correção é outro pacote.

Um pacote fechado pode ser **baixado** como arquivo e **importado** no painel de outro ambiente.
É o caminho que existe antes do envio direto, e o que continua valendo entre ambientes que não
se alcançam pela rede.

### 5.3 Envio

O painel de origem entrega o arquivo ao painel de destino por HTTP, pelo endereço público do
destino (o do proxy, com TLS), em partes, com retomada. Cada ligação entre dois ambientes tem uma
chave própria, configurada nos dois lados; sem ela o destino recusa. O destino confere o tamanho e
o SHA-256 e responde que recebeu. Para a origem, acabou: o pacote fica marcado como entregue.

Se o destino não aceitar conexões vindas de fora, a ligação é invertida na configuração: o destino
é que busca, de tempos em tempos, o que a origem tem para ele.

### 5.4 Aprovação

No destino o pacote aparece como **pendente**, com, para cada item:

- a comparação entre o que chega e o que existe hoje (linha a linha para banco; versão e lista de
  arquivos para pastas);
- **conflito**, quando o alvo no destino não está na versão de partida do item nem já na versão
  que o item traz;
- o que será parado e iniciado.

Quem aprova escolhe **aplicar agora** ou **agendar** para um dia e hora. Pode também recusar, com
o motivo. Um conflito não impede a aprovação, mas exige que ela seja dada sabendo dele.

### 5.5 Aplicação

O agente aplica os itens na ordem do pacote:

- **Banco:** executa o script com o usuário de banco da aplicação, lote por lote, em uma transação
  por item. Um objeto que já existe é alterado (`ALTER`) e não recriado, para não perder
  permissões nem quebrar quem depende dele. Antes, o painel guarda o script que estava lá.
- **Resposta que não chega.** Se o agente não responder a tempo, o item fica com resultado
  desconhecido e a aplicação para. Nada é tentado de novo sozinho.
- **Arquivos:** para o alvo (desliga o grupo, para o serviço, tira a aplicação do ar), copia a
  pasta atual para a guarda, troca os arquivos preservando a configuração, inicia de novo e espera
  o alvo responder (o processo no ar, o serviço rodando, a URL de verificação respondendo).

No primeiro item que falhar a aplicação para. O pacote fica **aplicado em parte**, com o que deu
certo, o que falhou e a mensagem. Nada é desfeito sozinho.

### 5.6 Desfazer

Por item, por decisão de alguém: reaplicar o script guardado, ou devolver a pasta guardada. Um
script livre só é desfeito se o pacote trouxe o `undo.sql`. São guardadas as últimas versões de
cada alvo (padrão: 3).

### 5.7 Promoção

Um pacote aplicado pode ser enviado ao ambiente seguinte, onde passa por tudo de novo.

## 6. Estados

Em cada ambiente um pacote está em um destes: **em montagem**, **fechado**, **enviado** (com o
destino e a hora da entrega), **pendente**, **recusado**, **aprovado** (agora ou agendado),
**aplicando**, **aplicado**, **aplicado em parte**, **falhou**. Toda passagem de um para outro
fica no histórico do pacote, com o usuário e a hora.

## 7. A garantia de leitura do banco

Hoje o agente só lê o catálogo, e um teste recusa qualquer consulta que faça outra coisa. O
transporte escreve no banco de propósito. As duas coisas ficam separadas:

- o leitor continua como está, com o mesmo teste;
- a escrita fica em um componente à parte, que só recebe o script de um item de um pacote
  aprovado, identificado pelo pacote e pelo item, e registra tudo o que executou;
- o painel continua sem poder mandar um texto qualquer para o banco: o único caminho é um pacote.

## 8. Configuração

No agente:

```json
"Transport": {
  "Environment": "DEV",
  "Inbox": "transporte\\entrada",
  "KeepVersions": 3,
  "Targets": [ { "Kind": "api", "Name": "api/cadastro", "Keep": ["appsettings*.json", "web.config"] } ]
}
```

No painel:

```json
"Transport": {
  "Environment": "DEV",
  "Links": [ { "To": "QAS", "Url": "https://qas.exemplo/zapmq", "Key": "…" } ]
}
```

## 9. Etapas

1. **Pacote de banco em um ambiente só:** área, montagem, pendência, comparação, aprovação
   (agora ou agendada), aplicação e histórico. Prova o modelo sem depender de rede.
2. **Arquivos:** micro serviços, serviços do Windows, APIs e módulos do frontend, com parada,
   guarda, troca e verificação.
3. **Envio entre ambientes**, com a chave da ligação e a conferência na chegada.
4. **Desfazer**, entrada por pasta e o que mais a prática pedir.

## 10. Em aberto

- A aplicação no banco só foi exercitada com um banco simulado; a primeira execução real será no
  primeiro destino que receber um pacote.
- Como tirar do ar uma aplicação que roda dentro do processo do servidor web.
- Os comandos de administração do agente chegam por uma fila do ZapMQ, e o protocolo de
  mensageria não tem autenticação. Antes de o transporte valer em produção, quem pode pedir uma
  aplicação ao agente precisa ser restrito.

## 11. O que está implementado

**Telas.** Item **Transporte** no menu, com um aviso de quantos pacotes esperam por alguém.

- **Área** (`/transporte`): o que espera para entrar em um pacote. Objetos entram pelo botão
  *Transportar* das telas Objetos e Alterações do banco (esta marca cada alteração como ainda
  não transportada, ou já em pacote); um script é escrito ali ou importado de um `.sql`. Fechar o
  pacote pede um nome e a descrição.
- **Pacotes** (`/transporte/pacotes`): os montados aqui e os que chegaram, com a situação de
  cada um, e *Importar pacote*.
- **Pacote** (`/transporte/pacotes/{id}`): os itens na ordem de aplicação, cada um com o que traz
  comparado linha a linha ao que existe aqui; a decisão (aprovar e aplicar agora, agendar,
  recusar, cancelar a aprovação); o resultado de cada item; e o histórico no ambiente.

**Como cada item é comparado com o destino:**

| Estado | Quando |
|---|---|
| novo aqui | o objeto não existe: será criado |
| já está igual | o que o pacote traz é o que está aqui |
| altera | existe e é diferente; está como a origem estava antes da mudança, ou não se sabe de onde a mudança partiu |
| diferente do ponto de partida | existe, e não é o que a origem tinha antes da mudança |
| já existe: precisa de script | tabela ou type que já existe e é diferente. **Impede a aprovação** |
| será apagado / já não existe | para um item de exclusão |

Além disso, um item que usa algo que não existe no destino nem vem no pacote é apontado.

**Ordem dos itens**, quando não é dada à mão: types, tabelas, scripts, functions, views,
procedures e, por fim, exclusões; dentro disso, cada objeto depois dos que ele usa.

**Aplicação.** O painel pede ao agente um item por vez (`DatabaseApply`), esperando até 6 minutos
por resposta. Cada item é uma transação. Antes de cada objeto, o script que estava no destino é
guardado com o resultado. A situação final é *aplicado*, *aplicado em parte* ou *falhou*.

**Excluir e recusar.** Um pacote **montado aqui** pode ser excluído: some deste ambiente, com o
arquivo, e os objetos que levava voltam a contar como não transportados. Um pacote **que chegou**
nunca é excluído: pode ser recusado, o que impede para sempre a aplicação dele aqui e mantém
registrado tudo o que trazia.

**Onde fica.** No painel, a pasta `transport` ao lado do executável (`ZapMQ:Transport:Directory`):
`area.json` e, por pacote, `packages/{id}/package.zpkg` e `state.json`. O nome do ambiente vem de
`ZapMQ:Transport:Environment`; sem ele, vale o nome da máquina.

**Rotas** (todas sob `api/transport`, atrás do login): `summary`; `area`, `area/objects`,
`area/scripts`, `area/{id}`; `packaged`; `packages`, `packages/import`, `packages/{id}`,
`packages/{id}/download`, `/check`, `/items/{n}`, `/approve`, `/reject`; `DELETE packages/{id}`.

**No agente:** o comando `DatabaseApply` e o componente `SqlServerWriter`, o único que escreve
no banco. Ele recusa um banco fora de `Databases`, cria ou altera conforme o objeto exista, não
recria tabela nem type, e escreve o `DROP` a partir do nome, nunca de um texto do pacote.

### 11.1 Arquivos (etapa 2)

**Alvos.** O agente monta sozinho a lista do que pode ser substituído na máquina:

| Tipo | De onde vem | Nome | O que para e inicia |
|---|---|---|---|
| `worker` | os grupos do `ConfigWorkers.json` | o nome do grupo | todos os grupos que rodam o programa da mesma pasta; só os que estavam ligados voltam |
| `service` | os serviços de `Services.Items` | o nome do serviço | o serviço |
| `api` | os sites do servidor web | o nome da pasta do site (a pasta de cima, quando a do site é só um número de instância) | os application pools dos sites que servem a pasta |
| `frontend` | os módulos do manifesto de `Frontends` | o nome do módulo | nada |

Sites que servem a mesma aplicação viram um alvo só, com todas as pastas, e todas são trocadas.
Um serviço executado por um empacotador (`nssm`) não diz onde está o programa: a pasta é informada
em `Transport.Targets`, que também serve para corrigir ou acrescentar qualquer alvo.

**Telas.** Transporte → *Aplicações* lista o que roda na máquina, dividido por tipo, com busca por
nome ou pasta. Cada aplicação tem a sua tela, com as pastas, os sites ou grupos que a servem e os
arquivos de configuração (seção 11.2). Transporte → *Configuração* guarda as pastas de entrada.

**Entrada.** Em Transporte → *Aplicações*, para cada alvo:

- *O que está rodando*: o agente empacota a pasta como está agora.
- *Enviar versão*: o `.zip` da pasta publicada, escolhido na máquina de quem está no painel, com
  ou sem a pasta de fora.
- *Versão nova na entrada*: aparece quando alguém deixou uma versão nova para aquele alvo na
  **pasta de entrada** do servidor. É o começo do caminho de uma versão: o programador copia a
  pasta publicada para a pasta de entrada do tipo, em uma pasta com o nome da aplicação como
  aparece na lista. A pasta de entrada de cada tipo (`api`, `worker`, `service`, `frontend`) é
  dita em Transporte → *Configuração* e criada ao salvar; sem dizer nada, é
  `transport\inbox\{tipo}`, ao lado do agente.
  Um `.zip` com o nome do alvo também serve. Levada à área, a versão sai da pasta de entrada. O
  que foi deixado com um nome que não é de nenhum alvo é apontado; o que ainda está sendo
  copiado (mexido há menos de 5 segundos) não é pego.

Nos dois casos o que é do ambiente fica de fora: `appsettings*.json`, `web.config`,
`ConfigWorkers.json`, bancos locais (`*.db`) e `logs/`.

**Aplicar onde o pacote foi montado.** Um pacote fechado pode ser aplicado no próprio ambiente,
com a mesma comparação e a mesma aprovação (agora ou agendada). É assim que a versão deixada na
pasta de entrada passa a rodar no primeiro ambiente; o mesmo pacote segue depois para o próximo.
Aplicado, o pacote não é mais excluído: fica como registro do que foi feito.

**Comparação no destino.** Arquivo por arquivo, pelo conteúdo: novo, alterado, removido ou igual.
Um alvo que o destino não tem aparece como *não existe aqui* e **impede a aprovação**: instalar o
que ainda não existe (seção 4.1) não está feito.

**Aplicação**, pelo agente, um alvo por vez:

1. para o que roda da pasta e espera parar (até 3 minutos); se não parar, nada é tocado e o que
   foi desligado volta a ligar;
2. guarda uma cópia da pasta inteira, com a configuração, em
   `transport/backup/{tipo}/{nome}/{data}-{pacote}`;
3. faz a pasta ter exatamente os arquivos do pacote, sem tocar no que é do ambiente; se não
   conseguir trocar todos, devolve a cópia antes de iniciar qualquer coisa;
4. inicia e espera estar no ar (até 3 minutos). Trocado e sem subir, o item **falha** com os
   arquivos novos no lugar, e diz isso;
5. mantém as últimas cópias de cada alvo (`Transport.KeepVersions`, padrão 3).

Um zip com arquivo que cairia fora da pasta de destino é recusado inteiro, antes de qualquer
escrita. O agente não substitui a si mesmo.

**Ordem em um pacote misto:** o banco primeiro, depois micro serviços, serviços e APIs, e por
último os módulos web.

**Painel e agente na mesma máquina.** Os arquivos passam de um para o outro por uma pasta local;
um agente em outra máquina não é atendido.

**Comandos do agente:** `TransportTargets`, `TransportTarget`, `TransportCapture` e
`TransportDeploy`. **Rotas novas:** `targets`, `area/running`, `area/incoming` e `area/upload`.

**Ainda não feito na etapa 2:**

- instalar um alvo que não existe no destino (seção 4.1);
- os arquivos da aplicação web que não são de um módulo (a casca que os carrega);
- a parada e a partida de sites no servidor web foram escritas e não puderam ser exercitadas fora
  do Windows: a primeira troca real de uma API é que as prova. O mesmo vale para serviços do
  Windows de verdade; o que foi exercitado são as pastas, os arquivos e a ordem dos passos.

### 11.2 Arquivos do ambiente

O que nunca viaja em um pacote é editado no próprio painel, na tela de cada aplicação.

- **Quais arquivos:** os que estão na pasta da aplicação, casam com a lista do que é do ambiente
  (`Transport.Keep`) e são texto (`.json`, `.config`, `.xml`, `.ini`, `.yml`, `.env` e
  semelhantes), até 1 MB. Bancos locais e logs não entram. Nada fora da pasta da aplicação é
  alcançado.
- **Salvar** só grava sobre a versão que foi aberta: se alguém mudou o arquivo nesse meio tempo, é
  preciso abrir de novo. Um `.json` que não é JSON válido não é gravado. A versão anterior fica em
  `transport\backup\config\{tipo}\{nome}\{data}`.
- **Reiniciar depois de salvar** é opcional e usa a mesma parada e partida do transporte.
- **Uma aplicação com mais de uma pasta** (instâncias) tem os arquivos de cada uma listados e
  editados à parte.
- **Fica registrado** quem abriu e quem gravou cada arquivo, no log do painel e no histórico do
  agente.

Estes arquivos costumam guardar senhas e cadeias de conexão. Quem entra no painel os vê, e o
pedido passa pela fila de administração do agente: vale o mesmo aviso da seção 10 sobre o
protocolo de mensageria não ter autenticação.

**Comandos do agente:** `TransportSettings`, `SetTransportInboxes`, `TransportFiles`,
`TransportFile` e `SetTransportFile`. **Rotas:** `settings`, `settings/inboxes`, `files` e `file`.
