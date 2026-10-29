# Endpoints das APIs

Na tela de uma API (*Processos e serviços → Aplicações → a API*), a aba **Endpoints** mostra o que
ela expõe e, quando a API diz, o que cada endpoint executa no banco. Nada disso é próprio de um
sistema: o ZapMQ lê dois arquivos abertos, cada um opcional.

| Fonte | O que dá | De onde vem |
|---|---|---|
| **OpenAPI** (`swagger.json`) | rotas, verbos, parâmetros, corpo e respostas | pedido à própria API, na máquina dela |
| **Mapa de endpoints** (`zapmq.endpoints.json`) | o SQL de cada endpoint e os objetos do banco que ele usa | arquivo na pasta publicada da API |

Com os dois, cada endpoint aparece documentado e com o que executa. Com um só, aparece o que ele
dá. Sem nenhum (uma API que não publica OpenAPI e não traz o mapa), a aba diz que o recurso não está
disponível para ela e como habilitar.

## 1. OpenAPI

O Worker Control, na máquina da API, pede o documento a `http://localhost:<porta>` de cada site da
API, até um responder. Os caminhos tentados, nesta ordem:

1. o configurado para a API (seção 4);
2. `/swagger/v1/swagger.json` (ASP.NET Core com Swashbuckle);
3. `/openapi/v1.json` (ASP.NET Core 9+);
4. `/swagger/doc/json` (Delphi com GBSwagger);
5. `/swagger.json`, `/openapi.json`, `/v3/api-docs` (outros).

O documento pode ser OpenAPI 3 ou Swagger 2. Uma resposta que exige login (401/403), que não é
JSON, ou que demora mais de 8 segundos, é dita como tal na tela. O resultado fica guardado por
5 minutos no painel; *Atualizar* pede de novo.

## 2. Mapa de endpoints

Um arquivo `zapmq.endpoints.json` na pasta publicada da API (a raiz, ao lado do executável). Como
está na pasta, viaja nos pacotes junto com a API: cada ambiente mostra o mapa do que está instalado
ali. Quem gera o arquivo é o projeto da API, no build, com a ferramenta que conhecer o seu código; o
ZapMQ só o lê.

```json
{
  "format": 1,
  "application": "Ojitech.Api.Cadastro",
  "version": "1.4.0",
  "builtAt": "2026-10-29T14:30:00Z",
  "generator": "endpoint-map 1.0",
  "endpoints": [
    {
      "method": "POST",
      "route": "/api/cadastro/antenaRFID",
      "handler": "AntenaRFIDController.Post",
      "commands": [
        {
          "sql": "EXEC spCadAntenaRFID @i_codigo = @i_codigo, @i_user = @i_user, @i_trans = @i_trans",
          "source": "AntenaRFIDRepository.SaveAsync",
          "dynamic": false,
          "notes": []
        }
      ],
      "objects": ["dbo.spCadAntenaRFID"]
    }
  ]
}
```

| Campo | Obrigatório | O que é |
|---|---|---|
| `format` | sim | `1` |
| `application`, `version`, `builtAt`, `generator` | não | de onde o mapa veio; `builtAt` (ISO 8601) é comparado com os binários da pasta |
| `endpoints[].method` | sim | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` |
| `endpoints[].route` | sim | a rota como o cliente chama, com os parâmetros entre chaves (`/api/x/{codigo}`) |
| `endpoints[].handler` | não | o que atende, em palavras do projeto |
| `endpoints[].commands[].sql` | sim | o texto do comando. Quando é montado em partes, o que varia vem marcado com `/*…*/` |
| `endpoints[].commands[].source` | não | onde no código o comando está |
| `endpoints[].commands[].dynamic` | não | `true` quando o texto é montado em execução (partes opcionais, valores interpolados) |
| `endpoints[].commands[].notes` | não | avisos em texto (por exemplo, valor colocado no texto em vez de parâmetro) |
| `endpoints[].objects` | não | objetos do banco usados, `schema.nome`. O ZapMQ também os tira do SQL, com o leitor de T-SQL |

O endpoint do mapa é casado com o do OpenAPI pelo verbo e pela rota, sem diferenciar maiúsculas e
sem olhar o nome dos parâmetros (`/x/{id}` casa com `/x/{codigo}`).

**Mapa desatualizado.** Quando há na pasta um binário (`.dll`, `.exe`) mais novo que o `builtAt`
(ou que o arquivo, sem `builtAt`), a tela avisa que o mapa pode não corresponder ao que está
instalado.

## 3. O banco

Os objetos de cada endpoint (os do campo `objects` e os lidos do SQL) são ligados ao catálogo do
banco que o ZapMQ acompanha: cada um abre a tela do objeto, com o que ele por sua vez usa. No
caminho inverso, a tela de um objeto do banco mostra **os endpoints que o usam**, em todas as APIs
deste ambiente que trazem o mapa, e um pacote que leva uma procedure, function, view ou tabela
mostra, no item, os endpoints deste ambiente que a usam.

## 4. Configuração

No agente, por API, quando o OpenAPI não está em nenhum dos caminhos acima:

```json
"Transport": {
  "OpenApiBy": { "api:Cadastro": "/docs/openapi.json" }
}
```

O valor é um caminho (pedido a cada site da API) ou um endereço completo. Também se muda na própria
aba, em *Onde está o OpenAPI*.
