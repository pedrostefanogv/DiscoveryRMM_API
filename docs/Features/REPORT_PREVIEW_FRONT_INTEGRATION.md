# Integracao de Preview de Relatorios no Frontend

## Objetivo

O endpoint `POST /api/reports/preview` foi desenhado para permitir que o frontend gere uma pre-visualizacao temporaria de um relatorio sem persistir execucao nem arquivo no banco, no disco local ou em S3.

Ele aceita:

- `templateId` para reutilizar um template salvo como base
- `template` inline para simular um template ainda nao cadastrado
- `filtersJson` para aplicar escopo global, client, site ou agent
- `responseDisposition` com `inline` ou `attachment`
- `previewMode` com `document` ou `html`

O endpoint `GET /api/reports/layout-schema` expõe capacidades para um builder visual de templates.

## Atualizacao de contrato (2026-03)

Esta secao documenta o contrato atual da API de relatorios e deve ser considerada como referencia principal para novas telas.

### Mudancas principais

- O backend trabalha com `datasetType` (enum) em vez de `datasetKey` para criacao/edicao/preview inline.
- O formato padrao do template e `defaultFormat` (nao `format`) para create/update/template draft.
- `GET /api/reports/datasets` agora retorna metadados de campos (`fieldMetadata`) e capacidades de join (`joinCapabilities`).
- `GET /api/reports/layout-schema` agora inclui `multiSource` com contrato de `dataSources` e regras de join.
- Novo endpoint `GET /api/reports/autocomplete` para sugerir campos no modo `alias.field`.

### Endpoints para builder moderno

```http
GET /api/reports/datasets
GET /api/reports/layout-schema
GET /api/reports/autocomplete?term=agent&datasetType=AgentHardware&alias=hw
```

### Contrato multi-fonte no layoutJson

Quando um relatorio usar mais de uma fonte, adicione `dataSources` no layout e referencie campos em `columns`, `groupBy`, `groupDetails` e `summaries` no formato `alias.field`.

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
    { "field": "hw.osName", "header": "SO" },
    { "field": "sw.softwareName", "header": "Software" }
  ],
  "summaries": [
    { "label": "Total linhas", "aggregate": "count" },
    { "label": "Softwares distintos", "field": "sw.softwareName", "aggregate": "countDistinct" }
  ]
}
```

### Payload atualizado de preview inline

```json
{
  "responseDisposition": "inline",
  "previewMode": "document",
  "format": "Pdf",
  "filtersJson": "{\"clientId\":\"550e8400-e29b-41d4-a716-446655440000\"}",
  "template": {
    "name": "Inventario consolidado preview",
    "datasetType": "AgentHardware",
    "defaultFormat": "Pdf",
    "layoutJson": "{\"title\":\"Inventario consolidado\",\"dataSources\":[{\"datasetType\":\"AgentHardware\",\"alias\":\"hw\"},{\"datasetType\":\"SoftwareInventory\",\"alias\":\"sw\",\"join\":{\"joinToAlias\":\"hw\",\"sourceKey\":\"agentId\",\"targetKey\":\"agentId\",\"joinType\":\"left\"}}],\"columns\":[{\"field\":\"hw.agentHostname\",\"header\":\"Agente\"},{\"field\":\"sw.softwareName\",\"header\":\"Software\"}] }"
  }
}
```

### Uso recomendado de autocomplete

1. Quando o usuario selecionar um dataset principal, chamar autocomplete com `datasetType` e `alias` padrao.
2. Quando incluir uma nova fonte em `dataSources`, chamar autocomplete novamente para popular campos daquela fonte.
3. Usar `items[].reference` (`alias.field`) como valor tecnico no builder.
4. Exibir `items[].field` e `items[].datasetName` como label para UX.

Exemplo de resposta resumida:

```json
{
  "fieldReferenceMode": "alias.field",
  "total": 2,
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
    },
    {
      "datasetType": "AgentHardware",
      "datasetKey": "agentHardware",
      "datasetName": "Agent Hardware",
      "field": "agentId",
      "reference": "hw.agentId",
      "dataType": "guid",
      "isJoinKey": true,
      "defaultAlias": "hw"
    }
  ]
}
```

## Fluxo recomendado no frontend

### Editor de template

O frontend pode manter um estado local com:

- `datasetType`
- `format`
- `layoutJson` montado a partir de formularios visuais
- `filtersJson`
- `templateId` opcional quando estiver editando um template existente

Cada ajuste relevante do usuario pode disparar um preview manual ou com debounce.

### Geracao do preview

Enviar `POST /api/reports/preview` com `responseDisposition = inline`.

Exemplo de payload:

```json
{
  "format": "Pdf",
  "responseDisposition": "inline",
  "filtersJson": "{\"clientId\":\"550e8400-e29b-41d4-a716-446655440000\",\"limit\":200}",
  "template": {
    "name": "Software por Agente",
    "datasetType": "SoftwareInventory",
    "layoutJson": "{\"title\":\"Software por Agente\",\"groupBy\":\"agentHostname\",\"hideGroupColumn\":true,\"groupTitleTemplate\":\"Agent {value}\",\"summaries\":[{\"label\":\"Total registros\",\"aggregate\":\"count\"}],\"groupSummaries\":[{\"label\":\"Softwares distintos\",\"field\":\"softwareName\",\"aggregate\":\"countDistinct\"}],\"columns\":[{\"field\":\"agentHostname\",\"header\":\"Agente\"},{\"field\":\"softwareName\",\"header\":\"Software\"},{\"field\":\"publisher\",\"header\":\"Fabricante\"},{\"field\":\"version\",\"header\":\"Versao\"}]}"
  }
}
```

Para descobrir capacidades do builder:

```http
GET /api/reports/layout-schema
```

## Formas de exibicao no frontend

### Opcao 1: iframe com Blob URL

Boa para PDF.

Fluxo:

1. Chamar a API com `fetch`.
2. Ler os headers `X-Report-RowCount`, `X-Report-Title` e `Content-Type`.
3. Converter a resposta em `Blob`.
4. Criar `URL.createObjectURL(blob)`.
5. Atribuir essa URL a um `iframe` ou abrir em nova aba.

Exemplo em JavaScript:

```ts
async function loadReportPreview(payload: unknown) {
  const response = await fetch('/api/reports/preview', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    const error = await response.json().catch(() => null);
    throw error ?? new Error('Falha ao gerar preview');
  }

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);

  return {
    url,
    contentType: response.headers.get('Content-Type'),
    rowCount: Number(response.headers.get('X-Report-RowCount') ?? '0'),
    title: response.headers.get('X-Report-Title') ?? 'Preview'
  };
}
```

### Opcao 2: nova aba

Boa para um preview mais simples sem viewer embutido.

1. Gera o Blob.
2. Abre `window.open(blobUrl, '_blank')`.

### Opcao 3: download imediato

Quando o usuario clicar em “Baixar preview”, enviar `responseDisposition = attachment`.

### Opcao 4: HTML direto para builder visual

Quando o objetivo for iterar layout rapidamente, enviar `previewMode = html`.

Nesse caso a API responde `text/html`, e o frontend pode usar `iframe.srcdoc = html` ou gerar um `Blob` com `text/html`.

## Estrategia de UI recomendada

### Painel esquerdo

- Dataset
- Escopo e filtros
- Campos/colunas
- Agrupamento
- Summary cards
- Estilo visual

### Painel direito

- Viewer do PDF em `iframe`
- Metadados do preview: total de linhas, titulo, formato
- Erros de validacao do layout ou filtros

## Campos importantes para um builder visual

### Agrupamento

- `groupBy`
- `groupTitleTemplate`
- `hideGroupColumn`
- `groupDetails[]`

### Tabela

- `columns[]`
- `sections[]`
- `format`
- `width`

Quando `sections[]` e usado, cada secao pode virar uma subtabela independente no preview PDF/HTML.

### Estilo

- `style.primaryColor`
- `style.headerBackgroundColor`
- `style.headerTextColor`
- `style.alternateRowColor`
- `style.borderColor`
- `style.fontFamily`
- `style.logoUrl`
- `style.logoMaxHeightPx`

### Sumarios

- `summaries[]` para cards gerais
- `groupSummaries[]` para cards por grupo

Agregadores suportados atualmente:

- `count`
- `countDistinct`
- `sum`

## Como pensar a Fase 2 no frontend

A Fase 2 nao e apenas “mais estilo”. Ela e o que transforma o template em um mini-descritor de apresentacao. Na pratica, o frontend pode ter um construtor visual que gera `layoutJson` sem exigir que o usuario escreva JSON manualmente.

### O que a Fase 2 viabiliza

1. Templates com agrupamento por entidade, como `Agent -> softwares`.
2. Cabecalho visual com branding.
3. Summary cards por relatorio e por grupo.
4. Reaproveitamento do mesmo dataset com apresentacoes diferentes.
5. Edicao iterativa antes de salvar o template definitivo.
6. Uso de preview HTML rapido no construtor e preview PDF como validacao final.

### Builder visual sugerido

1. O usuario escolhe o dataset.
2. O front consulta `GET /api/reports/datasets`.
3. O front consulta `GET /api/reports/layout-schema`.
4. O front monta selects para colunas, ordenacao, agrupamento, detalhe por grupo e estilo.
5. O front gera `layoutJson` a partir de componentes de UI.
6. O front chama `POST /api/reports/preview` com `previewMode = html` durante a edicao rapida.
7. O front chama `POST /api/reports/preview` com `format = Pdf` e `previewMode = document` para validacao final.
8. Quando o resultado agradar, o front chama `POST /api/reports/templates` para persistir.

## Observacoes praticas

### PDF em iframe

Na maioria dos browsers modernos, PDF funciona bem em `iframe` quando a API responde com `Content-Type: application/pdf` e `Content-Disposition: inline`.

### Controle de memoria no browser

Sempre revogue `Blob URLs` antigas:

```ts
URL.revokeObjectURL(oldUrl)
```

### Debounce

Nao gere preview a cada tecla pressionada em campos livres. Use debounce de 500ms a 1000ms ou um botao `Atualizar preview`.

### Tratamento de erro

Quando a API devolver `400`, mostre os erros de validacao do layout diretamente no builder. O endpoint ja retorna mensagens adequadas para orientar o usuario.

## Limites atuais

1. O preview hoje e mais forte em PDF do que em XLSX/CSV para visualizacao rica.
2. O builder ainda precisa ser implementado no frontend; o backend ja esta preparado para receber o `layoutJson` e expor o schema.
3. A renderizacao atual suporta cards de detalhe por grupo e subtabelas por `sections`, mas datasets profundamente aninhados ainda dependem de evolucoes adicionais de modelagem e query.