# Banco de dados

Especificação do monitoramento do banco de dados do ambiente pelo painel do ZapMQ. Complementa
[AMBIENTE.md](AMBIENTE.md): o Worker Control continua sendo o agente da máquina e o painel só
mostra o que ele lê.

## 1. Regras

- **Uma instância por servidor.** Cada Worker Control acompanha a instância que as aplicações da
  sua máquina usam, informada na configuração.
- **Nenhum dado de tabela sai da instância.** O agente só lê o que a instância diz de si mesma: o
  catálogo, as visões de gerenciamento e o histórico de backups e jobs. Isso não depende das
  permissões do usuário da conexão, que pode ser o mesmo das aplicações:
  - toda consulta enviada à instância está em um único arquivo (`Database/Queries.cs`), e um
    teste recusa qualquer uma que leia algo fora de `sys.` e `msdb.dbo.`, ou que não seja leitura;
  - o painel não envia texto de consulta ao agente: só pede partes pelo nome;
  - o texto dos comandos em execução e das consultas mais caras sai sem os valores escritos nele
    (textos entre aspas e números viram `?`, comentários são retirados).
- **A instância não é atrapalhada.** Cada conexão lê sem segurar bloqueio, espera no máximo 2 s por
  um e perde qualquer deadlock. As consultas têm limite de 10 s e rodam em uma linha de execução
  própria: uma instância lenta não atrasa a supervisão dos processos.
- **Só SQL Server**, atrás de uma interface (`IDatabaseSource`), sem nada específico de um cliente.

## 2. Configuração

Seção `Database` do `ConfigWorkers.json`:

```json
"Database": {
  "Name": "DEV",
  "Server": "servidor\\instancia",
  "User": "usuario",
  "Password": "senha",
  "Databases": ["MinhaBase"],
  "SampleSeconds": 30,
  "BlockingSeconds": 30,
  "BackupHours": 0,
  "DiskFreePercent": 10,
  "RetentionDays": 7
}
```

| Campo | Para quê |
|---|---|
| `Name` | Como a instância aparece no painel |
| `Server` | Endereço, como em uma connection string |
| `User`, `Password` | Sem usuário, vale a conta do serviço (autenticação do Windows) |
| `Encrypt`, `TrustServerCertificate` | Padrão: sem exigir criptografia e aceitando o certificado do servidor |
| `Databases` | Os bancos cujos objetos são listados (etapa 2). A saúde é da instância inteira |
| `BlockingSeconds` | A partir de quanto tempo uma sessão esperando por outra vira alerta |
| `BackupHours` | Idade máxima do último backup completo. Zero não olha backups |
| `DiskFreePercent` | Espaço livre mínimo nos discos dos arquivos dos bancos |

**Senha.** Digitada em texto no arquivo, ela é trocada na primeira leitura por uma versão cifrada
com a proteção de dados do Windows, na chave da máquina (`dpapi:...`). Só aquela máquina a lê de
volta; o arquivo copiado para outro lugar não serve. A cópia `.bak` que contivesse a senha em
texto é apagada. Para trocar a senha, basta digitar a nova no lugar.

## 3. Etapa 1: saúde

| Parte | De onde vem | Frequência |
|---|---|---|
| Identificação, tempo no ar | `SERVERPROPERTY`, `sys.dm_os_sys_info` | a cada leitura |
| Processador (SQL Server e resto da máquina) | `sys.dm_os_ring_buffers` | a cada leitura |
| Memória, conexões, comandos por segundo | `sys.dm_os_process_memory`, `sys.dm_os_performance_counters` | a cada leitura |
| Bancos: estado, tamanho, uso do log | `sys.databases`, `sys.master_files` | a cada leitura |
| Sessões por aplicação | `sys.dm_exec_sessions` | a cada leitura |
| Em execução e bloqueios | `sys.dm_exec_requests`, `sys.dm_exec_sql_text` | a cada leitura |
| Discos | `sys.dm_os_volume_stats` | a cada 5 min |
| Backups | `msdb.dbo.backupset` | a cada 5 min |
| Jobs | `msdb.dbo.sysjobs`, `sysjobservers` | a cada 5 min |
| Consultas mais caras | `sys.dm_exec_query_stats` | quando a tela pede |

Uma parte que o usuário da conexão não pode ler não derruba as outras: ela fica vazia e a tela diz
qual foi e o que a instância respondeu. Só a falha de conexão conta como instância fora do ar.

**Alertas** (aparecem na tela, em "Atenção" da Visão geral e no histórico de eventos): instância
fora do ar, banco fora de `ONLINE`, sessão bloqueada além do limite, disco abaixo do livre mínimo,
backup completo atrasado ou inexistente, job habilitado que falhou na última execução.

**Histórico.** Processador, memória, sessões, execuções, bloqueios e comandos por segundo ficam em
`database.db`, ao lado do serviço, pelo número de dias de `RetentionDays`.

**Contrato de administração:** `Database` (estado), `DatabaseHistory` (`Minutes` ou `From`/`To`) e
`DatabaseQueries`. No painel: `GET api/workers/database`, `/database/history`, `/database/queries`.

## 4. Etapa 2: objetos

Para cada banco de `Databases`: tabelas, views, procedures, functions e types definidos pelo
usuário, com busca e filtro por tipo e schema. Ao abrir um objeto:

- tabelas: colunas (tipo, tamanho, precisão, nulo, identity, default, calculada), chave primária,
  índices, chaves estrangeiras, checks, triggers, linhas e tamanho (de estatísticas, sem ler a tabela);
- views: colunas e script;
- procedures e functions: parâmetros (tipo, tamanho, saída, default), retorno e script;
- types: tipo base, ou as colunas quando for tipo de tabela;
- em todos: criado em, alterado em, e as dependências nos dois sentidos.

O script de views, procedures e functions é o que a instância guarda. O de tabelas e types é
montado pelo agente a partir do catálogo: equivalente, mas não idêntico ao de outras ferramentas.

## 5. Etapa 3: histórico de objetos

O agente guarda a impressão digital (hash do script normalizado) de cada objeto e registra quando
ela muda, com as duas versões. Responde "o que mudou no banco desde tal data".

## 6. Transporte entre ambientes (futuro)

Não faz parte destas etapas; fica registrado para que elas nasçam compatíveis.

**Fluxo pretendido:** da máquina do desenvolvedor, o item é colocado (talvez à mão) em uma área do
servidor de DEV; de DEV é transportado para uma área do QAS; do QAS, para uma área de PRD. Cada
servidor tem o seu ZapMQ, e o transporte passa de um ZapMQ para o outro. No destino, o que chega
fica pendente até ser aprovado e só então é aplicado.

**O que é transportado:** objetos de banco (como script de criação ou alteração), micro serviços,
APIs e módulos do frontend.

**O que já nasce pensando nisso:**

1. **Identidade de item:** tipo, nome e versão ou impressão digital, no mesmo formato para objeto
   de banco, executável e módulo do frontend.
2. **Versão de partida:** um pacote diz de qual impressão digital partiu. O destino em outra
   versão é um conflito, mostrado antes da aprovação.
3. **Aplicar é sempre um ato explícito**, uma única vez, com o resultado devolvido à origem.

**Em aberto para quando chegar a hora:** alteração de tabela que já existe (o destino precisa de
um `ALTER`, calculado ou escrito à mão); controle de acesso de verdade no painel, sem o qual
aprovar e aplicar não pode existir; onde ficam e como são as "áreas" de cada servidor.

## 7. Verificação

- Testes do agente com uma instância de mentira: alertas, eventos, senha, histórico, contrato.
- As consultas ao SQL Server só são provadas em uma instância real: o primeiro uso em DEV diz
  quais partes o usuário da conexão consegue ler.
