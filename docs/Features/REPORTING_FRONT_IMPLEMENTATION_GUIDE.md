# Implementacao Frontend: Relatorios e Preview

## Objetivo

Este documento consolida o contrato necessario para implementar no frontend o modulo de relatorios com:

- cadastro e edicao de templates;
- preview sem persistencia;
- preview em documento binario ou HTML;
- suporte a escopo global, cliente, site e agent;
- suporte a agrupamento, resumos, detalhes e secoes filhas;
- renderizacao com estilo orientado a layout.

O backend foi estruturado para que o frontend consiga montar um builder de template, validar configuracoes antes do envio e exibir previews temporarios sem criar execucoes nem gravar arquivos em storage.

## Atualizacao de contrato (2026-03)

As secoes abaixo registram o contrato atual da API. Para telas novas, priorize esta secao quando houver diferenca com exemplos antigos.

### Contrato atual de templates

- Campo de dataset: `datasetType` (enum), nao `datasetKey`.
- Campo de formato padrao: `defaultFormat`, nao `format`.
- Em preview com template inline, o draft tambem usa `datasetType` e `defaultFormat`.

Exemplo de create:

```json
{
	"name": "Inventario consolidado",
	"description": "Correlacao de hardware e software",
	"datasetType": "AgentHardware",
	"defaultFormat": "Pdf",
	"layoutJson": "{\"title\":\"Inventario consolidado\",\"columns\":[{\"field\":\"agentHostname\",\"header\":\"Agente\"}]}",
	"filtersJson": "{\"clientId\":null}",
	"createdBy": "frontend-user@meduza.local"
}
```

### Catalogo de datasets enriquecido

`GET /api/reports/datasets` retorna, alem de `type`, `key`, `name` e `fields`:

- `fieldMetadata[]` com `name`, `dataType`, `isJoinKey`.
- `joinCapabilities` com `supportsJoin`, `allowedJoinTypes`, `preferredKeys`, `defaultJoinType`.
- `supportsAsPrimarySource` e `supportsAsSecondarySource`.

### Schema de layout com multi-fonte

`GET /api/reports/layout-schema` inclui `multiSource`:

- `enabled`
- `aliasPattern`
- `allowedJoinTypes`
- `fieldReferenceMode` (valor atual: `alias.field`)
- `dataSourceContract.requiredFields`
- `dataSourceContract.joinFields`

### Novo endpoint de autocomplete

Use `GET /api/reports/autocomplete` para popular seletores de campo no builder.

Parametros:

- `term` (opcional)
- `datasetType` (opcional)
- `alias` (opcional)

Exemplo:

```http
GET /api/reports/autocomplete?term=agent&datasetType=AgentHardware&alias=hw
```

Resposta resumida:

```json
{
	"fieldReferenceMode": "alias.field",
	"total": 3,
	"items": [
		{
			"datasetType": "AgentHardware",
			"datasetKey": "agentHardware",
			"datasetName": "Agent Hardware",
			"field": "agentHostname",
			"reference": "hw.agentHostname",
			"dataType": "text",
			"isJoinKey": false,
			"defaultAlias": "hw"
		}
	]
}
```

### Layout multi-fonte (dataSources)

Para correlacionar mais de uma fonte no mesmo relatorio, use `dataSources` no `layoutJson`.

Exemplo:

```json
{
	"title": "Inventario consolidado",
	"dataSources": [
		{
			"datasetType": "AgentHardware",
			"alias": "hw"
		},
		{
			"datasetType": "SoftwareInventory",
			"alias": "sw",
			"join": {
				"joinToAlias": "hw",
				"sourceKey": "agentId",
				"targetKey": "agentId",
				"joinType": "left"
			}
		}
	],
	"columns": [
		{ "field": "hw.agentHostname", "header": "Agente" },
		{ "field": "hw.osName", "header": "Sistema operacional" },
		{ "field": "sw.softwareName", "header": "Software" }
	],
	"summaries": [
		{ "label": "Total linhas", "aggregate": "count" },
		{ "label": "Softwares distintos", "field": "sw.softwareName", "aggregate": "countDistinct" }
	]
}
```

Regras importantes:

- A primeira fonte pode ser sem `join`; a partir da segunda, `join` e obrigatorio.
- `joinType` aceito: `left` ou `inner`.
- Referencia de campo em modo multi-fonte deve seguir `alias.field`.

### Agregadores suportados

Atualmente os agregadores suportados no layout sao:

- `count`
- `countDistinct`
- `sum`

## Conceitos principais

### 1. Dataset

Cada relatorio nasce de um dataset. O dataset define quais dados podem ser consultados e quais filtros de execucao sao aceitos.

### 2. Template

O template define:

- nome;
- dataset;
- formato final salvo;
- escopo do template;
- filtros padrao opcionais;
- layout visual e estrutural em JSON.

### 3. Preview

O preview pode usar:

- um template ja salvo, referenciado por id;
- um template inline enviado no corpo da requisicao, sem persistir no banco.

O preview pode responder em dois modos:

- document: retorna PDF, XLSX ou CSV;
- html: retorna HTML pronto para exibicao em iframe, modal ou aba de preview.

### 4. Escopo

Os dados podem ser filtrados e simulados por:

- global;
- client;
- site;
- agent.

Esse recorte nao depende de salvar o template antes. O preview usa os filtros enviados na requisicao.

## Fluxo recomendado para o frontend

### Builder de template

Fluxo sugerido:

1. Carregar catalogo de datasets.
2. Carregar schema de layout.
3. Usuario escolhe dataset, formato, nome e layout.
4. Usuario ajusta filtros de preview por client, site, agent ou outros filtros do dataset.
5. Front chama preview com template inline.
6. Quando o resultado estiver satisfatorio, front persiste o template.

### Edicao de template existente

Fluxo sugerido:

1. Buscar template por id.
2. Popular formulario e builder visual.
3. Permitir alterar layout e filtros.
4. Chamar preview com `templateId` e, se necessario, sobrescrever `template` no corpo para testar alteracoes ainda nao salvas.
5. Salvar com endpoint de update.

## Endpoints

Os endpoints abaixo ficam sob a base:

`/api/reports`

### 1. Catalogo de datasets

`GET /api/reports/datasets`

Retorna todos os datasets disponiveis e seus filtros suportados.

Uso no frontend:

- preencher select de dataset;
- montar filtros dinamicos de execucao;
- limitar builder conforme dataset selecionado.

Resposta esperada:

```json
[
	{
		"key": "agents.inventory",
		"name": "Inventario de agentes",
		"description": "Dados consolidados de agentes e inventario",
		"supportedFormats": ["pdf", "xlsx", "csv"],
		"defaultFormat": "pdf",
		"filters": [
			{
				"name": "clientId",
				"type": "uuid",
				"required": false,
				"label": "Cliente"
			},
			{
				"name": "siteId",
				"type": "uuid",
				"required": false,
				"label": "Site"
			},
			{
				"name": "agentId",
				"type": "uuid",
				"required": false,
				"label": "Agent"
			}
		]
	}
]
```

Observacao:

- os datasets exatos disponiveis dependem do backend;
- o frontend deve tratar o payload como dinamico e nao hardcoded.

### 2. Schema do layout para builder

`GET /api/reports/layout-schema`

Retorna metadados para o frontend montar o builder e validar UI antes do envio.

Resposta esperada:

```json
{
	"previewModes": ["document", "html"],
	"responseDispositions": ["inline", "attachment"],
	"supportedOrientations": ["portrait", "landscape"],
	"supportedColumnFormats": ["text", "number", "currency", "percent", "date", "datetime", "boolean"],
	"supportedSummaryAggregates": ["count", "sum", "avg", "min", "max"],
	"limits": {
		"maxLayoutJsonLength": 40000,
		"maxColumns": 25,
		"maxSections": 10,
		"maxSectionColumns": 10,
		"maxSummaries": 10,
		"maxGroupDetails": 8
	},
	"notes": [
		"columns e sections nao podem coexistir na mesma definicao raiz",
		"groupDetails so faz sentido quando groupBy estiver preenchido"
	]
}
```

Uso no frontend:

- controlar dropdowns com valores suportados;
- limitar quantidade de colunas, secoes e resumos;
- exibir mensagens preventivas antes de chamar a API.

### 3. Listar templates

`GET /api/reports/templates`

Retorna a lista paginada ou enumerada de templates cadastrados.

Uso no frontend:

- listagem principal de templates;
- tela de selecao para execucao ou edicao.

### 4. Buscar template por id

`GET /api/reports/templates/{templateId}`

Uso no frontend:

- abrir formulario de edicao;
- duplicar template;
- carregar layout salvo para preview.

### 5. Criar template

`POST /api/reports/templates`

Payload:

```json
{
	"name": "Softwares por agent",
	"datasetKey": "software.inventory",
	"description": "Lista softwares agrupados por agent",
	"format": "pdf",
	"scopeType": "global",
	"filtersJson": "{\"clientId\": null, \"siteId\": null, \"agentId\": null}",
	"layoutJson": "{\"title\":\"Softwares por agent\",\"groupBy\":\"agentName\",\"columns\":[{\"field\":\"softwareName\",\"label\":\"Software\"}]}"
}
```

Campos principais:

- `name`: nome visivel do template.
- `datasetKey`: chave do dataset retornada no catalogo.
- `description`: opcional.
- `format`: `pdf`, `xlsx` ou `csv`.
- `scopeType`: escopo do template salvo.
- `filtersJson`: JSON serializado com filtros padrao.
- `layoutJson`: JSON serializado com o layout.

### 6. Atualizar template

`PUT /api/reports/templates/{templateId}`

Mesmo contrato do create.

Uso no frontend:

- salvar alteracoes do builder;
- publicar versao ajustada apos validar via preview.

### 7. Excluir template

`DELETE /api/reports/templates/{templateId}`

Uso no frontend:

- remocao em listagem ou tela de detalhes.

### 8. Preview temporario

`POST /api/reports/preview`

Esse e o endpoint central do frontend.

Ele nao persiste template, nao cria execucao historica e nao envia artefato para storage temporario.

#### Payload

```json
{
	"templateId": "00000000-0000-0000-0000-000000000000",
	"template": {
		"name": "Softwares por agent",
		"datasetKey": "software.inventory",
		"description": "Preview temporario",
		"format": "pdf",
		"scopeType": "global",
		"filtersJson": "{\"clientId\":\"11111111-1111-1111-1111-111111111111\"}",
		"layoutJson": "{\"title\":\"Softwares por agent\",\"groupBy\":\"agentName\",\"groupTitleTemplate\":\"Agent: {{agentName}}\",\"groupDetails\":[{\"field\":\"agentVersion\",\"label\":\"Versao\"}],\"columns\":[{\"field\":\"softwareName\",\"label\":\"Software\"},{\"field\":\"softwareVersion\",\"label\":\"Versao\"}]}"
	},
	"format": "pdf",
	"filtersJson": "{\"clientId\":\"11111111-1111-1111-1111-111111111111\",\"siteId\":null,\"agentId\":null}",
	"fileName": "softwares-por-agent-preview",
	"responseDisposition": "inline",
	"previewMode": "document"
}
```

#### Regras de uso

- `templateId` e opcional.
- `template` e opcional.
- deve existir pelo menos uma origem valida de template.
- se os dois forem enviados, o backend pode usar o template salvo como base e aplicar sobrescritas do `template` inline.
- `format` pode sobrescrever temporariamente o formato para preview.
- `filtersJson` pode sobrescrever os filtros padrao do template.
- `responseDisposition` define como o browser deve tratar o retorno.
- `previewMode` define se a resposta sera documento binario ou HTML.

#### Valores aceitos

- `responseDisposition`: `inline` ou `attachment`
- `previewMode`: `document` ou `html`
- `format`: `pdf`, `xlsx`, `csv`

#### Resposta quando `previewMode = document`

Headers importantes:

- `Content-Type`: depende do formato.
- `Content-Disposition`: inline ou attachment com filename.
- `X-Report-Preview`: `true`
- `X-Report-RowCount`: quantidade de linhas retornadas.
- `X-Report-Title`: titulo resolvido para o preview.
- `X-Report-Format`: formato final retornado.

Tipos de retorno comuns:

- PDF: `application/pdf`
- XLSX: `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`
- CSV: `text/csv`

Uso no frontend:

- PDF inline: Blob + `iframe` ou nova aba.
- XLSX/CSV attachment: download direto.

#### Resposta quando `previewMode = html`

Headers importantes:

- `Content-Type`: `text/html; charset=utf-8`
- `X-Report-Preview`: `true`
- `X-Report-RowCount`: quantidade de linhas.
- `X-Report-Title`: titulo resolvido.

Uso no frontend:

- injetar em `iframe.srcdoc`;
- ou criar Blob HTML e abrir em aba/modal.

HTML preview e o melhor modo para iteracao rapida no builder visual.

### 9. Executar relatorio persistido

`POST /api/reports/run`

Esse endpoint e diferente de preview.

Ele representa a execucao formal do relatorio e pode gerar historico, registro de execucao e armazenamento conforme configuracao do backend.

Payload tipico:

```json
{
	"templateId": "00000000-0000-0000-0000-000000000000",
	"filtersJson": "{\"clientId\":\"11111111-1111-1111-1111-111111111111\"}",
	"requestedBy": "frontend-user@meduza.local"
}
```

Uso no frontend:

- botao de gerar relatorio definitivo;
- historico de execucoes;
- download posterior.

### 10. Listar execucoes

`GET /api/reports/executions`

Uso no frontend:

- historico geral;
- grid de execucoes recentes;
- status de processamentos assicronos, se aplicavel.

### 11. Buscar execucao por id

`GET /api/reports/executions/{executionId}`

Uso no frontend:

- tela de detalhe da execucao;
- consultar status, nome, formato e artefato gerado.

### 12. Download de execucao

`GET /api/reports/executions/{executionId}/download`

Uso no frontend:

- download do relatorio definitivo gerado anteriormente.

## Tipos e enums

### ReportFormat

Valores aceitos:

- `pdf`
- `xlsx`
- `csv`

### PreviewMode

Valores aceitos:

- `document`
- `html`

### ResponseDisposition

Valores aceitos:

- `inline`
- `attachment`

### ScopeType

Valores comuns esperados:

- `global`
- `client`
- `site`
- `agent`

O frontend deve preferir usar os valores efetivamente retornados pelo backend quando houver endpoint ou DTO com esses enums serializados.

## Contrato do template

### Template salvo

Exemplo de shape esperado no frontend:

```json
{
	"id": "00000000-0000-0000-0000-000000000000",
	"name": "Softwares por agent",
	"description": "Agrupado por agent",
	"datasetKey": "software.inventory",
	"format": "pdf",
	"scopeType": "global",
	"filtersJson": "{\"clientId\":null,\"siteId\":null,\"agentId\":null}",
	"layoutJson": "{...}",
	"createdAtUtc": "2025-01-15T18:00:00Z",
	"updatedAtUtc": "2025-01-15T18:10:00Z"
}
```

Observacao importante:

- `filtersJson` e `layoutJson` sao strings JSON serializadas;
- no frontend, o ideal e parsear para objeto ao editar e reserializar no submit.

## Contrato do layoutJson

O `layoutJson` define a apresentacao do dataset. O backend trata o dataset como tabela canonica, e o layout decide como exibir, agrupar e resumir os dados.

### Shape geral

```json
{
	"title": "Softwares por agent",
	"subtitle": "Cliente XPTO",
	"orientation": "landscape",
	"logoUrl": "https://cdn.exemplo/logo.png",
	"groupBy": "agentName",
	"groupTitleTemplate": "Agent: {{agentName}}",
	"groupTitlePrefix": "Agent",
	"hideGroupColumn": true,
	"columns": [
		{
			"field": "softwareName",
			"label": "Software",
			"format": "text",
			"width": "40%",
			"align": "left"
		},
		{
			"field": "softwareVersion",
			"label": "Versao",
			"format": "text",
			"align": "left"
		},
		{
			"field": "installedAt",
			"label": "Instalado em",
			"format": "date",
			"align": "center"
		}
	],
	"groupDetails": [
		{
			"field": "agentVersion",
			"label": "Versao do agent"
		},
		{
			"field": "siteName",
			"label": "Site"
		}
	],
	"summaries": [
		{
			"field": "softwareName",
			"label": "Total de softwares",
			"aggregate": "count"
		}
	],
	"groupSummaries": [
		{
			"field": "softwareName",
			"label": "Qtd por agent",
			"aggregate": "count"
		}
	],
	"style": {
		"primaryColor": "#0F4C81",
		"secondaryColor": "#E9F1F7",
		"accentColor": "#1F7A8C",
		"headerTextColor": "#FFFFFF",
		"fontFamily": "Segoe UI, sans-serif",
		"showRowStripes": true
	}
}
```

## Campos do layout

### Campos raiz

- `title`: titulo principal do relatorio.
- `subtitle`: subtitulo opcional.
- `orientation`: `portrait` ou `landscape`.
- `logoUrl`: URL externa ou data URL suportada pelo backend.
- `groupBy`: nome da coluna do dataset usada para agrupar linhas.
- `groupTitleTemplate`: template do titulo do grupo com placeholders como `{{agentName}}`.
- `groupTitlePrefix`: prefixo de fallback quando nao houver template completo.
- `hideGroupColumn`: oculta a coluna usada no agrupamento dentro da tabela principal.
- `columns`: colunas exibidas na tabela principal.
- `sections`: secoes filhas, usadas para compor subtabelas ou blocos adicionais.
- `groupDetails`: pares label/valor exibidos acima da tabela do grupo.
- `summaries`: resumos globais.
- `groupSummaries`: resumos por grupo.
- `style`: definicoes visuais.

### Regra importante

No layout raiz, `columns` e `sections` nao devem coexistir quando a configuracao resultar em estrutura ambigua. O frontend deve bloquear essa combinacao conforme orientacao do endpoint `layout-schema`.

### ColumnDefinition

```json
{
	"field": "softwareName",
	"label": "Software",
	"format": "text",
	"width": "40%",
	"align": "left"
}
```

Campos:

- `field`: nome do campo do dataset.
- `label`: titulo exibido.
- `format`: formato visual. Valores suportados pelo backend sao retornados por `layout-schema`.
- `width`: largura opcional para HTML/PDF.
- `align`: alinhamento visual, normalmente `left`, `center`, `right`.

### GroupDetailDefinition

```json
{
	"field": "siteName",
	"label": "Site"
}
```

Uso:

- exibir cards ou pares label/valor no topo de cada grupo;
- bom para metadados do agent, cliente, site, sistema operacional e afins.

### SummaryDefinition

```json
{
	"field": "softwareName",
	"label": "Total de softwares",
	"aggregate": "count"
}
```

Aggregates suportados:

- `count`
- `sum`
- `avg`
- `min`
- `max`

### SectionDefinition

As sections permitem compor subtabelas ou areas adicionais quando um dataset expoe colunas relacionadas que devam ser apresentadas de forma separada.

Exemplo:

```json
{
	"title": "Aplicativos instalados",
	"source": "softwareItems",
	"columns": [
		{
			"field": "name",
			"label": "Nome"
		},
		{
			"field": "version",
			"label": "Versao"
		}
	]
}
```

Uso no frontend:

- permitir adicionar subtabelas por secao;
- ideal para cenarios de dados hierarquicos ou blocos derivados.

### StyleDefinition

Exemplo:

```json
{
	"primaryColor": "#0F4C81",
	"secondaryColor": "#F4F8FB",
	"accentColor": "#2C6E49",
	"headerTextColor": "#FFFFFF",
	"fontFamily": "Segoe UI, sans-serif",
	"showRowStripes": true
}
```

Uso:

- cabecalhos coloridos;
- contraste de texto;
- linhas alternadas;
- identidade visual no PDF e no HTML preview.

## Exemplo de template agrupado: Agent -> softwares

Esse e o caso de uso citado na analise inicial.

Objetivo:

- agrupar por agent;
- exibir detalhes do agent no topo;
- listar softwares instalados por grupo;
- mostrar total por grupo e total geral.

### Layout sugerido

```json
{
	"title": "Inventario de softwares por agent",
	"subtitle": "Preview operacional",
	"orientation": "landscape",
	"groupBy": "agentName",
	"groupTitleTemplate": "Agent: {{agentName}}",
	"hideGroupColumn": true,
	"groupDetails": [
		{ "field": "clientName", "label": "Cliente" },
		{ "field": "siteName", "label": "Site" },
		{ "field": "agentVersion", "label": "Versao do agent" },
		{ "field": "osName", "label": "Sistema operacional" }
	],
	"columns": [
		{ "field": "softwareName", "label": "Software" },
		{ "field": "softwareVersion", "label": "Versao" },
		{ "field": "publisher", "label": "Fabricante" },
		{ "field": "installedAt", "label": "Instalado em", "format": "date" }
	],
	"groupSummaries": [
		{ "field": "softwareName", "label": "Total no agent", "aggregate": "count" }
	],
	"summaries": [
		{ "field": "softwareName", "label": "Total geral", "aggregate": "count" }
	],
	"style": {
		"primaryColor": "#16324F",
		"secondaryColor": "#EEF4F7",
		"accentColor": "#3A7D44",
		"headerTextColor": "#FFFFFF",
		"showRowStripes": true
	}
}
```

## Filtros

### filtersJson

`filtersJson` e uma string contendo JSON serializado.

Exemplo:

```json
{
	"clientId": "11111111-1111-1111-1111-111111111111",
	"siteId": null,
	"agentId": null,
	"search": "chrome"
}
```

Boas praticas no frontend:

- manter objeto tipado internamente;
- serializar com `JSON.stringify` apenas no envio;
- ao receber template salvo, fazer `JSON.parse` com fallback seguro.

## Estrategias de preview no frontend

### Preview rapido no builder

Melhor escolha:

- `previewMode = html`
- `responseDisposition = inline`

Motivo:

- iteracao mais rapida;
- renderizacao simples em iframe;
- menor atrito que lidar com Blob PDF durante a configuracao visual.

### Preview final antes de salvar

Melhor escolha:

- `previewMode = document`
- `format = pdf`
- `responseDisposition = inline`

Motivo:

- valida exatamente o resultado final do PDF.

### Download temporario para XLSX e CSV

Melhor escolha:

- `previewMode = document`
- `format = xlsx` ou `csv`
- `responseDisposition = attachment`

## Tratamento de respostas no frontend

### PDF inline

Fluxo sugerido:

1. Fazer request com `responseType = blob`.
2. Criar `URL.createObjectURL(blob)`.
3. Exibir em iframe ou abrir nova aba.
4. Ler headers `X-Report-Title` e `X-Report-RowCount` para UI auxiliar.

### HTML preview

Fluxo sugerido:

1. Fazer request esperando texto.
2. Ler HTML bruto.
3. Injetar em `iframe.srcdoc` ou Blob HTML.
4. Exibir ao lado do builder.

### Download attachment

Fluxo sugerido:

1. Ler header `Content-Disposition`.
2. Extrair `filename` quando existir.
3. Disparar download do Blob.

## Validacoes importantes para o frontend

Antes de enviar para o backend, a UI deve tentar prevenir:

- layout sem `title` quando a experiencia exigir titulo obrigatorio;
- `groupDetails` sem `groupBy`;
- excesso de colunas;
- excesso de secoes;
- excesso de resumos;
- cor em formato invalido;
- combinacao estrutural nao suportada entre `columns` e `sections`;
- formato de preview nao compativel com a acao do usuario.

Mesmo com essas validacoes, o backend continua como fonte final de verdade.

## Erros esperados

### 400 Bad Request

Causas comuns:

- `layoutJson` invalido;
- `filtersJson` invalido;
- dataset inexistente;
- formato nao suportado;
- template ausente no preview;
- campo ou aggregate nao suportado.

O frontend deve exibir a mensagem da API e, quando possivel, destacar a area do builder relacionada.

### 404 Not Found

Causas comuns:

- template inexistente;
- execucao inexistente.

### 500 Internal Server Error

Causas comuns:

- erro de renderizacao;
- dataset inconsistente;
- falha de infraestrutura.

Nesses casos, o frontend deve preservar o estado do builder e permitir nova tentativa.

## Recomendacoes de implementacao

### Tipagem no frontend

Criar tipos locais equivalentes para:

- `ReportTemplate`
- `PreviewReportRequest`
- `ReportLayoutDefinition`
- `ReportLayoutColumnDefinition`
- `ReportLayoutSummaryDefinition`
- `ReportLayoutSectionDefinition`
- `ReportLayoutStyleDefinition`
- `DatasetCatalogItem`
- `DatasetFilterDefinition`
- `LayoutSchemaResponse`

### Estado de tela

Separar estados em:

- configuracao do template;
- filtros de preview;
- resultado do preview;
- erros de validacao local;
- erro da API.

### Auto-save local do builder

Opcional, mas recomendado:

- manter draft local no estado ou storage do browser;
- so persistir no backend quando usuario confirmar salvar template.

## Exemplo de requests para o frontend

### Preview HTML com template inline

```http
POST /api/reports/preview
Content-Type: application/json

{
	"template": {
		"name": "Preview HTML",
		"datasetKey": "software.inventory",
		"format": "pdf",
		"scopeType": "global",
		"layoutJson": "{\"title\":\"Softwares por agent\",\"groupBy\":\"agentName\",\"columns\":[{\"field\":\"softwareName\",\"label\":\"Software\"}]}",
		"filtersJson": "{\"clientId\":null,\"siteId\":null,\"agentId\":null}"
	},
	"previewMode": "html",
	"responseDisposition": "inline"
}
```

### Preview PDF inline

```http
POST /api/reports/preview
Content-Type: application/json

{
	"templateId": "00000000-0000-0000-0000-000000000000",
	"format": "pdf",
	"filtersJson": "{\"clientId\":\"11111111-1111-1111-1111-111111111111\"}",
	"previewMode": "document",
	"responseDisposition": "inline"
}
```

### Preview CSV com download

```http
POST /api/reports/preview
Content-Type: application/json

{
	"templateId": "00000000-0000-0000-0000-000000000000",
	"format": "csv",
	"previewMode": "document",
	"responseDisposition": "attachment"
}
```

## Limitacoes atuais

- o frontend nao deve assumir que todo dataset suporta qualquer campo no layout; os campos dependem do dataset retornado pela API;
- HTML preview e pensado para iteracao visual, nao como artefato oficial persistido;
- a expressividade de `sections` depende da forma como o dataset entrega dados relacionados;
- templates salvos continuam armazenando `layoutJson` e `filtersJson` como strings JSON.

## Resumo pratico

Para implementar o frontend com seguranca:

1. Use `GET /api/reports/datasets` para popular dataset e filtros.
2. Use `GET /api/reports/layout-schema` para montar o builder.
3. Modele `layoutJson` como objeto no frontend e serialize apenas no submit.
4. Use `POST /api/reports/preview` com template inline durante a edicao.
5. Use `previewMode = html` para iteracao rapida.
6. Use `previewMode = document` com PDF para validacao final.
7. Salve com `POST /api/reports/templates` ou `PUT /api/reports/templates/{id}` apenas quando o preview estiver aprovado.
8. Use `POST /api/reports/run` e endpoints de `executions` para fluxo definitivo e historico.
