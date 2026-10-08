# Transporte entre ambientes

Especificação do transporte e da aprovação de mudanças entre ambientes (por exemplo DEV → QAS →
PRD) pelo painel do ZapMQ. **Ainda não implementado**: este documento é o que foi combinado e o
que falta decidir. Complementa [AMBIENTE.md](AMBIENTE.md) e [BANCO.md](BANCO.md).

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

Tabela não tem tipo próprio: mudança de tabela é sempre um **script livre**, escrito por quem fez
e revisado na aprovação. Calcular a alteração a partir de duas definições pode perder dados.

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
agente. Um item cujo alvo não existe no destino aparece como problema antes da aprovação.

## 5. O caminho de uma mudança

### 5.1 Entrada

De duas formas, que dão no mesmo lugar, a **área** do ambiente:

- **Pelo painel:** escolher objetos do banco (o script é o atual, lido pelo agente), escrever um
  script livre, ou enviar o zip de uma pasta publicada dizendo o tipo e o alvo.
- **Por pasta:** uma pasta de entrada observada pelo agente, com uma subpasta por tipo
  (`banco/`, `worker/`, `service/`, `api/`, `frontend/`). O nome do arquivo é o nome do alvo:
  `api/cadastro.zip`, `banco/ajuste-pedido.sql`.

Em DEV também se pode montar um item **a partir do que está rodando**: o agente empacota a pasta
atual do alvo.

### 5.2 Montagem

Na área, escolhem-se os itens, a ordem e a descrição, e o pacote é fechado. Fechado, não muda
mais: uma correção é outro pacote.

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
  por item quando o script permite. Antes, guarda o script atual do objeto.
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

- Onde aplicar durante os testes da etapa 1, já que em DEV os objetos são alterados direto no
  banco: um banco de rascunho na mesma instância.
- O apelido de cada banco.
- Como tirar do ar uma aplicação que roda dentro do processo do servidor web.
