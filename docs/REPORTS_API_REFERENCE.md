# 📊 API de Relatórios - Referência para Front-end

## Visão Geral

Base URL: `/api/reports`

Formatos Suportados:
- ✅ `Xlsx` (Excel)
- ✅ `Csv` (CSV)
- ✅ `Pdf` (PDF via Playwright)

---

## 🔍 Catálogo de Datasets

### GET `/api/reports/datasets`

Lista os datasets disponíveis e seus campos permitidos.

**Resposta:**
```json
[
  {
    "type": "SoftwareInventory",
    "fields": ["clientId", "siteId", "agentId", "softwareName", "publisher", "version", "installedAt"],
    "formats": ["Xlsx", "Csv", "Pdf"]
  },
  {
    "type": "Logs",
    "fields": ["clientId", "siteId", "agentId", "type", "level", "source", "from", "to", "message"],
    "formats": ["Xlsx", "Csv", "Pdf"]
  },
  {
    "type": "ConfigurationAudit",
    "fields": ["entityType", "entityId", "fieldName", "oldValue", "newValue", "changedBy", "changedAt", "reason"],
    "formats": ["Xlsx", "Csv", "Pdf"]
  },
  {
    "type": "Tickets",
    "fields": ["clientId", "siteId", "agentId", "workflowStateId", "priority", "createdAt", "closedAt", "slaBreached"],
    "formats": ["Xlsx", "Csv", "Pdf"]
  },
  {
    "type": "AgentHardware",
    "fields": ["clientId", "siteId", "agentId", "osName", "processor", "totalMemoryGB", "collectedAt"],
    "formats": ["Xlsx", "Csv", "Pdf"]
  }
]
```

---

## 📋 Templates de Relatórios

### GET `/api/reports/templates`

Lista todos os templates de relatórios.

**Query Parameters:**
- `datasetType` (opcional): Filtra por tipo de dataset (`SoftwareInventory`, `Logs`, `ConfigurationAudit`, `Tickets`, `AgentHardware`)
- `isActive` (opcional, default: `true`): Filtra templates ativos/inativos

**Resposta:**
```json
[
  {
    "id": "a47f4f44-1b06-4a9c-b180-20b9b0074c8b",
    "name": "Agent Hardware Inventory",
    "description": "Current hardware specifications of all agents",
    "datasetType": "AgentHardware",
    "defaultFormat": "Xlsx",
    "layoutJson": "{\"title\":\"Agent Hardware Inventory\",\"columns\":[...]}",
    "filtersJson": "{\"limit\":10000,\"orderBy\":\"collectedAt\"}",
    "isActive": true,
    "version": 1,
    "createdAt": "2026-03-07T23:00:12Z",
    "updatedAt": "2026-03-07T23:00:12Z",
    "createdBy": "migration",
    "updatedBy": "migration"
  }
]
```

---

### GET `/api/reports/templates/{id}`

Obtém um template específico.

**Resposta:** objeto `ReportTemplate` (mesmo formato do array acima)

---

### POST `/api/reports/templates`

Cria um novo template de relatório.

**Request Body:**
```json
{
  "name": "Meu Relatório Customizado",
  "description": "Descrição opcional do relatório",
  "datasetType": "Logs",
  "defaultFormat": "Pdf",
  "layoutJson": "{\"title\":\"System Logs\",\"columns\":[{\"field\":\"timestamp\",\"width\":20,\"header\":\"Data\"}],\"pageSize\":100}",
  "filtersJson": "{\"level\":[\"Error\",\"Warning\"],\"daysBack\":7}",
  "createdBy": "usuario@exemplo.com"
}
```

**Validações:**
- `name`: 2-200 caracteres, obrigatório
- `description`: máximo 2000 caracteres, opcional
- `datasetType`: enum válido
- `defaultFormat`: deve estar nos formatos habilitados (`Xlsx`, `Csv`, `Pdf`)
- `layoutJson`: JSON válido, obrigatório
- `filtersJson`: JSON válido ou null

**Resposta (201 Created):**
```json
{
  "id": "019ccaab-2617-76fe-b783-34b42040782f",
  "name": "Meu Relatório Customizado",
  "version": 1,
  ...
}
```

---

### PUT `/api/reports/templates/{id}`

Atualiza um template existente (cria novo snapshot no histórico).

**Request Body:** (todos os campos opcionais)
```json
{
  "name": "Nome Atualizado",
  "description": "Nova descrição",
  "datasetType": "Logs",
  "defaultFormat": "Xlsx",
  "layoutJson": "{...}",
  "filtersJson": "{...}",
  "isActive": true,
  "updatedBy": "usuario@exemplo.com"
}
```

**Resposta (200 OK):** template atualizado com `version` incrementada

---

### DELETE `/api/reports/templates/{id}`

Remove um template (soft delete - mantém histórico).

**Resposta (204 No Content)**

---

### GET `/api/reports/templates/{id}/history`

Obtém o histórico de versões de um template.

**Query Parameters:**
- `limit` (opcional, default: 50): número máximo de versões

**Resposta:**
```json
[
  {
    "id": "019ccaab-3456-7890-abcd-1234567890ab",
    "templateId": "019ccaab-2617-76fe-b783-34b42040782f",
    "version": 2,
    "eventType": "Updated",
    "name": "Nome Atualizado",
    "datasetType": "Logs",
    "defaultFormat": "Xlsx",
    "layoutJson": "{...}",
    "filtersJson": "{...}",
    "isActive": true,
    "createdAt": "2026-03-07T23:45:00Z",
    "createdBy": "usuario@exemplo.com"
  },
  {
    "id": "019ccaab-2617-76fe-b783-34b42040782f",
    "templateId": "019ccaab-2617-76fe-b783-34b42040782f",
    "version": 1,
    "eventType": "Created",
    ...
  }
]
```

**Event Types:**
- `Created`: template criado
- `Updated`: template atualizado
- `Deleted`: template removido

---

## 💾 Armazenamento de Relatórios

### Onde os arquivos são salvos?

Quando um relatório é processado, o arquivo gerado é armazenado em:

```
[APP_BASE_DIRECTORY]/report-exports/report-{executionId}.{extension}
```

**Exemplos:**
- PDF: `/app/report-exports/report-019ccad9679d74668a20179c79d34ddd.pdf` (87.247 bytes)
- Excel: `/app/report-exports/report-019ccad9679d74668a20179c79d34ddd.xlsx`
- CSV: `/app/report-exports/report-019ccad9679d74668a20179c79d34ddd.csv`

**Informações no Banco de Dados:**

A tabela `report_executions` armazena:
- `id`: UUID único da execução
- `template_id`: referência ao template
- `result_path`: caminho completo do arquivo gerado
- `result_content_type`: tipo MIME (`application/pdf`, `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`, `text/csv`)
- `result_size_bytes`: tamanho em bytes
- `row_count`: número de linhas no relatório
- `status`: `Completed`, `Failed`, `Pending`, `Running`
- `error_message`: mensagem de erro (se houver)
- `execution_time_ms`: tempo de processamento em milissegundos

---

## ▶️ Execução de Relatórios

### POST `/api/reports/run`

Executa um relatório (síncrono ou assíncrono).

**Request Body:**
```json
{
  "templateId": "a47f4f44-1b06-4a9c-b180-20b9b0074c8b",
  "format": "Pdf",
  "filtersJson": "{\"clientId\":\"019ccaa6-2d6d-71d5-b41f-45cb7e0deffe\",\"siteId\":\"019ccaa6-2e30-7890-abcd-1234567890ab\"}",
  "createdBy": "usuario@exemplo.com",
  "runAsync": true
}
```

**Campos:**
- `templateId`: GUID do template (obrigatório)
- `format`: `Xlsx`, `Csv` ou `Pdf` (opcional, usa defaultFormat do template)
- `filtersJson`: JSON com filtros runtime (opcional, usa filtersJson do template)
  - Pode incluir: `clientId`, `siteId`, `agentId`, `from`, `to`, `limit`, etc.
- `createdBy`: identificação do usuário (opcional, máx 256 chars)
- `runAsync`: 
  - `true` → enfileira e retorna imediatamente (202 Accepted)
  - `false` → processa e aguarda conclusão (200 OK com download path)

**Resposta Assíncrona (202 Accepted):**
```json
{
  "executionId": "019ccad9-679d-7466-8a20-179c79d34ddd",
  "status": "Pending",
  "message": "Report execution queued for async processing."
}
```

**Resposta Síncrona (200 OK):**
```json
{
  "executionId": "019ccad9-679d-7466-8a20-179c79d34ddd",
  "status": "Completed",
  "rowCount": 1234,
  "contentType": "application/pdf",
  "resultSizeBytes": 87247,
  "downloadPath": "/api/reports/executions/019ccad9-679d-7466-8a20-179c79d34ddd/download"
}
```

**Status possíveis:**
- `Pending`: aguardando processamento
- `Running`: em execução
- `Completed`: concluído com sucesso
- `Failed`: falha (veja `errorMessage`)

---

### GET `/api/reports/executions/{id}`

Consulta o status de uma execução.

**Query Parameters:**
- `clientId` (opcional): ID do cliente para validação de escopo

**Resposta:**
```json
{
  "id": "019ccad9-679d-7466-8a20-179c79d34ddd",
  "templateId": "209569a9-6b8d-4891-ac6c-148ea012cf42",
  "format": "Pdf",
  "status": "Completed",
  "resultPath": "reports/019ccad9-679d-7466-8a20-179c79d34ddd.pdf",
  "resultContentType": "application/pdf",
  "resultSizeBytes": 87247,
  "rowCount": 145,
  "executionTimeMs": 3542,
  "createdAt": "2026-03-07T23:50:10Z",
  "startedAt": "2026-03-07T23:50:12Z",
  "finishedAt": "2026-03-07T23:50:16Z",
  "createdBy": "usuario@exemplo.com"
}
```

---

### GET `/api/reports/executions`

Lista as execuções recentes.

**Query Parameters:**
- `clientId` (opcional): filtra por cliente
- `limit` (opcional, default: 50): máximo de registros

**Resposta:** array de `ReportExecution`

---

### GET `/api/reports/executions/{id}/download`

Faz download do relatório gerado.

**Query Parameters:**
- `clientId` (opcional): ID do cliente para validação de escopo

**Resposta:** arquivo binário com headers:
- `Content-Type`: `application/pdf` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | `text/csv`
- `Content-Disposition`: `attachment; filename="report_YYYYMMDD_HHmmss.{ext}"`
- `Content-Length`: tamanho em bytes

---

## 📥 Como Baixar um Relatório

### Fluxo Básico:

#### 1️⃣ Disparar Geração (Assíncrono)
```javascript
const runResponse = await fetch('/api/reports/run', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    templateId: '209569a9-6b8d-4891-ac6c-148ea012cf42',
    format: 'Pdf',
    runAsync: true
  })
});

const { executionId } = await runResponse.json();
console.log('Execution ID:', executionId);
// Output: 019ccad9-679d-7466-8a20-179c79d34ddd
```

#### 2️⃣ Aguardar Conclusão (Polling)
```javascript
async function waitForReport(executionId) {
  for (let attempt = 0; attempt < 30; attempt++) {
    const response = await fetch(`/api/reports/executions/${executionId}`);
    const execution = await response.json();
    
    console.log(`[${attempt + 1}/30] Status: ${execution.status}`);
    
    if (execution.status === 'Completed') {
      return execution;
    } else if (execution.status === 'Failed') {
      throw new Error(`Report failed: ${execution.errorMessage}`);
    }
    
    // Aguarda 2 segundos antes de tentar novamente
    await new Promise(resolve => setTimeout(resolve, 2000));
  }
  
  throw new Error('Report generation timeout (60 segundos)');
}

const completed = await waitForReport(executionId);
console.log('Relatório concluído!');
console.log('Tamanho:', completed.resultSizeBytes, 'bytes');
console.log('Linhas:', completed.rowCount);
console.log('Tempo:', completed.executionTimeMs, 'ms');
```

#### 3️⃣ Fazer Download
```javascript
// Opção A: Redirecionar para o download
function downloadReport(executionId) {
  window.location.href = `/api/reports/executions/${executionId}/download`;
  // O navegador abrirá o diálogo de download automaticamente
}

downloadReport(completed.id);
```

```javascript
// Opção B: Download com fetch (mais controle)
async function downloadReportAsBlob(executionId, fileName) {
  const response = await fetch(`/api/reports/executions/${executionId}/download`);
  
  if (!response.ok) {
    throw new Error(`Download failed: ${response.statusText}`);
  }
  
  const blob = await response.blob();
  const url = window.URL.createObjectURL(blob);
  
  // Criar link temporário
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName || `report_${new Date().toISOString()}.pdf`;
  document.body.appendChild(a);
  a.click();
  
  // Limpar recursos
  document.body.removeChild(a);
  window.URL.revokeObjectURL(url);
}

downloadReportAsBlob(completed.id, 'meu-relatorio.pdf');
```

---

### Fluxo Completo em Uma Função
```javascript
async function generateAndDownloadReport(
  templateId,
  format = 'Pdf',
  fileName = null,
  clientId = null
) {
  try {
    // 1. Disparar execução
    console.log('📊 Iniciando geração de relatório...');
    const runResponse = await fetch('/api/reports/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        templateId,
        format,
        runAsync: true,
        createdBy: 'usuario@exemplo.com'
      })
    });
    
    if (!runResponse.ok) {
      throw new Error(`Failed to start report: ${runResponse.statusText}`);
    }
    
    const { executionId } = await runResponse.json();
    console.log(`✓ Relatório enfileirado: ${executionId}`);
    
    // 2. Aguardar conclusão (com timeout)
    let execution = null;
    for (let i = 0; i < 60; i++) {
      const statusResponse = await fetch(
        `/api/reports/executions/${executionId}${
          clientId ? `?clientId=${clientId}` : ''
        }`
      );
      
      if (!statusResponse.ok) {
        throw new Error('Failed to check report status');
      }
      
      execution = await statusResponse.json();
      
      if (execution.status === 'Completed') {
        console.log(`✓ Relatório concluído em ${execution.executionTimeMs}ms`);
        break;
      } else if (execution.status === 'Failed') {
        throw new Error(`Relatório falhou: ${execution.errorMessage}`);
      }
      
      console.log(`⏳ Aguardando... (${i + 1}/60)`);
      await new Promise(r => setTimeout(r, 1000));
    }
    
    if (!execution || execution.status !== 'Completed') {
      throw new Error('Report generation timeout');
    }
    
    // 3. Download
    console.log('📥 Baixando relatório...');
    const downloadResponse = await fetch(
      `/api/reports/executions/${executionId}/download${
        clientId ? `?clientId=${clientId}` : ''
      }`
    );
    
    if (!downloadResponse.ok) {
      throw new Error('Failed to download report');
    }
    
    const blob = await downloadResponse.blob();
    const url = window.URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName || 
      `report-${format.toLowerCase()}-${new Date().getTime()}.${
        format === 'Pdf' ? 'pdf' :
        format === 'Xlsx' ? 'xlsx' :
        'csv'
      }`;
    
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    window.URL.revokeObjectURL(url);
    
    console.log(`✓ Relatório baixado: ${a.download}`);
    
    return {
      success: true,
      executionId,
      fileName: a.download,
      sizeBytes: execution.resultSizeBytes,
      rowCount: execution.rowCount,
      executionTimeMs: execution.executionTimeMs
    };
    
  } catch (error) {
    console.error('❌ Erro ao gerar relatório:', error);
    return {
      success: false,
      error: error.message
    };
  }
}

// Uso:
const result = await generateAndDownloadReport(
  '209569a9-6b8d-4891-ac6c-148ea012cf42', // templateId
  'Pdf',                                      // formato
  'meu-relatorio.pdf',                       // nome do arquivo
  '019ccaa6-2d6d-71d5-b41f-45cb7e0deffe'  // clientId (opcional)
);

if (result.success) {
  console.log(`✓ Sucesso! Arquivo: ${result.fileName}`);
  console.log(`  Tamanho: ${(result.sizeBytes / 1024).toFixed(2)} KB`);
  console.log(`  Linhas: ${result.rowCount}`);
  console.log(`  Tempo: ${result.executionTimeMs}ms`);
}
```

---

### Com SignalR (Tempo Real)
```javascript
// Conectar ao hub de notificações
const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/notifications')
  .withAutomaticReconnect()
  .build();

connection.on('report.completed', (notification) => {
  console.log('📊 Relatório concluído!', notification.payload);
  // Trigger download automaticamente
  window.location.href = notification.payload.downloadPath;
});

connection.on('report.failed', (notification) => {
  console.error('❌ Relatório falhou!', notification.payload.error);
});

await connection.start();

// Disparar relatório
const runResponse = await fetch('/api/reports/run', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    templateId: '209569a9-6b8d-4891-ac6c-148ea012cf42',
    format: 'Pdf',
    runAsync: true
  })
});

const { executionId } = await runResponse.json();
console.log(`✓ Aguardando notificação para ${executionId}...`);
// O SignalR notificará quando o relatório estiver pronto!
```

---

## 📋 Endpoints de Download Resumidos

| Operação | Método | Endpoint | Descrição |
|----------|--------|----------|-----------|
| **Disparar Relatório** | POST | `/api/reports/run` | Enfileira ou processa relatório |
| **Verificar Status** | GET | `/api/reports/executions/{id}` | Consulta o status da execução |
| **Listar Execuções** | GET | `/api/reports/executions` | Lista histórico de relatórios |
| **Baixar Relatório** | GET | `/api/reports/executions/{id}/download` | ⬇️ **Faz o download do arquivo** |

---

## 🎯 Ciclo Completo de um Relatório

```
┌─────────────────────────────────────────────────────────────┐
│ 1. POST /api/reports/run                                    │
│    ├─ templateId (obrigatório)                              │
│    ├─ format ("Pdf", "Xlsx", "Csv")                         │
│    ├─ filtersJson (filtros runtime)                         │
│    └─ runAsync (true = assíncrono, false = síncrono)        │
└──────────────────┬──────────────────────────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────────────────────────┐
│ 2. Resposta:                                                │
│    { executionId: "019ccad9-679d-...", status: "Pending" } │
└──────────────────┬──────────────────────────────────────────┘
                   │
        ┌──────────┴──────────┐
        │                     │
        ▼                     ▼
  SÍNCRONO               ASSÍNCRONO
  (runAsync=false)       (runAsync=true)
        │                     │
        │                     ├─ Background Service processa
        │                     ├─ Queries dataset
        │                     ├─ Renderiza PDF/Excel/CSV
        │                     ├─ Salva em: /report-exports/
        │                     └─ Publica notificação
        │                     
        └──────────┬──────────┘
                   │
                   ▼
┌─────────────────────────────────────────────────────────────┐
│ 3. GET /api/reports/executions/{executionId}               │
│    Response: {                                              │
│      status: "Completed",                                   │
│      resultPath: "/app/report-exports/report-019ccad9.pdf", │
│      resultSizeBytes: 87247,                                │
│      rowCount: 145,                                         │
│      executionTimeMs: 3542                                  │
│    }                                                        │
└──────────────────┬──────────────────────────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────────────────────────┐
│ 4. GET /api/reports/executions/{executionId}/download      │
│    ├─ Verifica se arquivo existe                           │
│    ├─ Lê arquivo do disco                                  │
│    └─ Retorna como attachment (inicia download)            │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔍 Exemplo de Resposta Completa

```json
{
  "id": "019ccad9-679d-7466-8a20-179c79d34ddd",
  "templateId": "209569a9-6b8d-4891-ac6c-148ea012cf42",
  "clientId": "019ccaa6-2d6d-71d5-b41f-45cb7e0deffe",
  "format": "Pdf",
  "status": "Completed",
  "resultPath": "C:\\App\\report-exports\\report-019ccad9679d74668a20179c79d34ddd.pdf",
  "resultContentType": "application/pdf",
  "resultSizeBytes": 87247,
  "rowCount": 145,
  "errorMessage": null,
  "executionTimeMs": 3542,
  "createdAt": "2026-03-07T23:50:10.000Z",
  "startedAt": "2026-03-07T23:50:12.000Z",
  "finishedAt": "2026-03-07T23:50:15.584Z",
  "createdBy": "usuario@exemplo.com"
}
```

**Campos importantes:**
- ✅ `status`: se for "Completed", pode fazer download
- ✅ `resultSizeBytes`: tamanho em bytes (87KB no exemplo)
- ✅ `rowCount`: linhas incluídas (145 no exemplo)
- ✅ `executionTimeMs`: tempo de processamento (3.5 segundos)
- ✅ `resultPath`: caminho no servidor (interno)

---

## ⚡ Erros Comuns no Download

### ❌ Status 404 - Relatório não encontrado
```javascript
// Causa: executionId inválido ou relatório expirou
// Solução: Verificar se o ID está correto
console.log(`Procurando: /api/reports/executions/${executionId}`);
```

### ❌ Status 400 - Arquivo não existe mais
```javascript
// Causa: arquivo foi deletado do disco
// Solução: Regenerar o relatório
await fetch('/api/reports/run', {...});
```

### ❌ Status 202 ao fazer download
```javascript
// Causa: relatório ainda está processando (status = "Pending")
// Solução: Aguardar mais tempo antes de tentar download
for (...) {
  status = await checkStatus(executionId);
  if (status === 'Completed') break;
}
```

---

## 📐 Estrutura de Armazenamento

```
[AppDirectory]/
├── report-exports/
│   ├── report-019ccad9679d7466.pdf      (87 KB)
│   ├── report-019ccaab26177890.xlsx    (245 KB)
│   ├── report-019ccaab26177891.csv     (12 KB)
│   └── report-019ccaab26177892.pdf     (156 KB)
├── appsettings.json
├── bin/
└── ...
```

**Tamanhos típicos:**
- PDF: 50-500 KB (dependendo das imagens)
- Excel: 100-1000 KB (mais comprimido)
- CSV: 10-100 KB (muito leve)

---

## 🧹 Limpeza de Arquivos Antigos

O sistema atualmente **não deleta automaticamente** os arquivos gerados. Para produção, recomenda-se:

```csharp
// Exemplo: deletar relatórios com mais de 30 dias
var olderThan = DateTime.UtcNow.AddDays(-30);
var files = Directory.GetFiles(outputDirectory, "report-*.pdf");

foreach (var file in files)
{
    var fileInfo = new FileInfo(file);
    if (fileInfo.CreationTime < olderThan)
    {
        File.Delete(file);
    }
}
```

Ou configurar um job de limpeza no PostgreSQL:

```sql
-- Opcionalmente: deletar execuções com mais de 30 dias
DELETE FROM report_executions 
WHERE status = 'Completed' 
  AND finished_at < (NOW() - INTERVAL '30 days');
```

---

**Exemplo de Teste (curl):**
```bash
# Download direto via curl
curl -X GET http://localhost:5299/api/reports/executions/019ccad9-679d-7466-8a20-179c79d34ddd/download \
  -o meu-relatorio.pdf

# Com cliente ID
curl -X GET "http://localhost:5299/api/reports/executions/019ccad9-679d-7466-8a20-179c79d34ddd/download?clientId=019ccaa6-2d6d-71d5-b41f-45cb7e0deffe" \
  -o meu-relatorio.pdf

# Verificar headers
curl -X GET http://localhost:5299/api/reports/executions/019ccad9-679d-7466-8a20-179c79d34ddd/download \
  -I  # mostra apenas headers (Content-Type, Content-Length, etc)
```

---

**Exemplo de uso:**
```typescript
async function downloadReport(executionId: string) {
  const response = await fetch(`/api/reports/executions/${executionId}/download`);
  const blob = await response.blob();
  const url = window.URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `report_${executionId}.pdf`;
  a.click();
}
```

---

## 🔔 Notificações (SignalR)

Quando um relatório assíncrono é concluído, o sistema publica notificações via:

1. **SignalR Hub** (`/hubs/notifications`):
   - Tópico: `report.completed` ou `report.failed`
   - Payload: `{ executionId, templateName, status, rowCount, downloadPath }`

2. **Persistência** (GET `/api/notifications`):
   - Notificações ficam salvas e podem ser consultadas posteriormente
   - Tipos: `Informational`, `Warning`, `Critical`
   - Destinatários: usuários, agentes ou chaves customizadas

---

## 💡 Exemplo de Fluxo Completo

### 1. Listar Templates Disponíveis
```javascript
const response = await fetch('/api/reports/templates?isActive=true');
const templates = await response.json();
```

### 2. Executar Relatório (Assíncrono)
```javascript
const execution = await fetch('/api/reports/run', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    templateId: templates[0].id,
    format: 'Pdf',
    filtersJson: JSON.stringify({
      clientId: currentClientId,
      from: '2026-01-01T00:00:00Z',
      to: '2026-12-31T23:59:59Z'
    }),
    runAsync: true
  })
});
const { executionId } = await execution.json();
```

### 3. Polling de Status (ou aguardar notificação SignalR)
```javascript
async function waitForCompletion(executionId) {
  while (true) {
    const response = await fetch(`/api/reports/executions/${executionId}`);
    const exec = await response.json();
    
    if (exec.status === 'Completed') {
      return exec.downloadPath;
    } else if (exec.status === 'Failed') {
      throw new Error(exec.errorMessage);
    }
    
    await new Promise(resolve => setTimeout(resolve, 2000));
  }
}
```

### 4. Download do Relatório
```javascript
const downloadPath = await waitForCompletion(executionId);
window.location.href = downloadPath; // ou use fetch + blob
```

---

## 📐 Estrutura do LayoutJson

O campo `layoutJson` define como o relatório será renderizado:

```json
{
  "title": "Título do Relatório",
  "columns": [
    {
      "field": "agentId",
      "width": 20,
      "header": "Agent ID",
      "format": "text"
    },
    {
      "field": "timestamp",
      "width": 20,
      "header": "Data/Hora",
      "format": "datetime"
    },
    {
      "field": "message",
      "width": 50,
      "header": "Mensagem"
    }
  ],
  "pageSize": 500,
  "orientation": "portrait"
}
```

**Formatos suportados no campo `format`:**
- `text` (padrão)
- `datetime`
- `number`
- `currency`

---

## 🔧 Estrutura do FiltersJson

Filtros comuns para queries:

```json
{
  "clientId": "guid-do-cliente",
  "siteId": "guid-do-site",
  "agentId": "guid-do-agent",
  "from": "2026-01-01T00:00:00Z",
  "to": "2026-12-31T23:59:59Z",
  "limit": 10000,
  "orderBy": "timestamp",
  "orderDirection": "DESC",
  "level": ["Error", "Warning"],
  "status": ["Open", "InProgress"],
  "daysBack": 30
}
```

**Filtros específicos por dataset:**

- **Logs**: `level`, `type`, `source`, `from`, `to`, `daysBack`
- **Tickets**: `status`, `priority`, `workflowStateId`, `from`, `to`
- **SoftwareInventory**: `softwareName`, `publisher`, `version`
- **ConfigurationAudit**: `entityType`, `from`, `to`, `daysBack`
- **AgentHardware**: `osName`, `minMemoryGB`, `maxMemoryGB`

---

## ⚠️ Limites e Validações

- **Tamanho máximo de resposta**: 10.000 linhas (configurável via `limit`)
- **Range de datas**: recomendado não exceder 1 ano
- **Timeout de execução síncrona**: 60 segundos (use `runAsync: true` para grandes volumes)
- **Nome de template**: 2-200 caracteres
- **Descrição**: até 2000 caracteres
- **CreatedBy/UpdatedBy**: até 256 caracteres
- **FiltersJson e LayoutJson**: devem ser JSON válidos

---

## 🔐 Autorização

**Client Scope:**
- Templates com `clientId` = null são **gerais** (usados por qualquer cliente)
- Templates com `clientId` específico são **privados** daquele cliente
- Execuções filtram automaticamente dados pelo `clientId` fornecido nos filtros

**Permissões recomendadas:**
- Visualizar templates: qualquer usuário autenticado
- Criar/editar templates: admin ou role específica
- Executar relatórios: qualquer usuário (com scope do seu cliente)
- Baixar relatórios: apenas o criador ou admin

---

## 📞 Suporte para Notificações

O sistema de relatórios está integrado ao sistema de notificações multipropósito:

**Eventos publicados:**
- `report.completed`: relatório concluído com sucesso
- `report.failed`: erro na geração

**Severidades:**
- `Informational`: conclusão normal
- `Warning`: avisos durante processamento
- `Critical`: falha crítica

**Destinatários:**
- `RecipientUserId`: notificação para usuário específico
- `RecipientAgentId`: notificação para agent específico
- `RecipientKey`: chave customizada (ex: email do solicitante)

---

## 🧪 Exemplo de Teste (curl)

```bash
# 1. Listar templates
curl -X GET http://localhost:5299/api/reports/templates

# 2. Executar relatório
curl -X POST http://localhost:5299/api/reports/run \
  -H "Content-Type: application/json" \
  -d '{
    "templateId": "209569a9-6b8d-4891-ac6c-148ea012cf42",
    "format": "Pdf",
    "runAsync": true
  }'

# 3. Verificar status
curl -X GET http://localhost:5299/api/reports/executions/019ccad9-679d-7466-8a20-179c79d34ddd

# 4. Download
curl -X GET http://localhost:5299/api/reports/executions/019ccad9-679d-7466-8a20-179c79d34ddd/download \
  --output relatorio.pdf
```

---

## 📝 TypeScript Type Definitions

```typescript
// Enums
enum ReportDatasetType {
  SoftwareInventory = 0,
  Logs = 1,
  ConfigurationAudit = 2,
  Tickets = 3,
  AgentHardware = 4
}

enum ReportFormat {
  Xlsx = 0,
  Csv = 1,
  Pdf = 2
}

enum ReportExecutionStatus {
  Pending = 0,
  Running = 1,
  Completed = 2,
  Failed = 3
}

enum NotificationSeverity {
  Informational = 0,
  Warning = 1,
  Critical = 2
}

// DTOs
interface ReportTemplate {
  id: string;
  clientId?: string;
  name: string;
  description?: string;
  datasetType: ReportDatasetType;
  defaultFormat: ReportFormat;
  layoutJson: string;
  filtersJson?: string;
  isActive: boolean;
  version: number;
  createdAt: string;
  updatedAt: string;
  createdBy?: string;
  updatedBy?: string;
}

interface CreateReportTemplateRequest {
  name: string;
  description?: string;
  datasetType: ReportDatasetType;
  defaultFormat: ReportFormat;
  layoutJson: string;
  filtersJson?: string;
  createdBy?: string;
}

interface UpdateReportTemplateRequest {
  name?: string;
  description?: string;
  datasetType?: ReportDatasetType;
  defaultFormat?: ReportFormat;
  layoutJson?: string;
  filtersJson?: string;
  isActive?: boolean;
  updatedBy?: string;
}

interface RunReportRequest {
  templateId: string;
  format?: ReportFormat;
  filtersJson?: string;
  createdBy?: string;
  runAsync: boolean;
}

interface ReportExecution {
  id: string;
  templateId: string;
  clientId?: string;
  format: ReportFormat;
  filtersJson?: string;
  status: ReportExecutionStatus;
  resultPath?: string;
  resultContentType?: string;
  resultSizeBytes?: number;
  rowCount?: number;
  errorMessage?: string;
  executionTimeMs?: number;
  createdAt: string;
  startedAt?: string;
  finishedAt?: string;
  createdBy?: string;
}

interface ReportTemplateHistory {
  id: string;
  templateId: string;
  version: number;
  eventType: 'Created' | 'Updated' | 'Deleted';
  name: string;
  datasetType: ReportDatasetType;
  defaultFormat: ReportFormat;
  layoutJson: string;
  filtersJson?: string;
  isActive: boolean;
  createdAt: string;
  createdBy?: string;
}
```

---

**Última atualização:** 7 de março de 2026  
**Versão da API:** 1.0  
**Geração de PDF:** ✅ Habilitado (Playwright.NET)
