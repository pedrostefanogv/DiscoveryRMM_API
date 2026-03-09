# 🎯 Resumo Rápido - API de Criação de Relatórios

## 📌 Endpoints Essenciais

| Método | Endpoint | Ação |
|--------|----------|------|
| **POST** | `/api/reports/templates` | ✅ Criar novo template |
| **PUT** | `/api/reports/templates/{id}` | ✏️ Editar template |
| **DELETE** | `/api/reports/templates/{id}` | 🗑️ Deletar template |
| **GET** | `/api/reports/templates` | 📋 Listar todos |
| **GET** | `/api/reports/templates/{id}` | 🔍 Detalhes |
| **GET** | `/api/reports/templates/{id}/history` | 📜 Histórico versões |
| **POST** | `/api/reports/run` | ▶️ Executar relatório |

---

## ⚡ Quick Create (Curl)

```bash
curl -X POST http://localhost:5299/api/reports/templates \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Software Report",
    "datasetType": 0,
    "defaultFormat": 0,
    "layoutJson": {
      "title": "Software",
      "columns": [
        {"field": "siteName", "header": "Site", "width": 20},
        {"field": "softwareName", "header": "Software", "width": 30}
      ]
    },
    "filtersJson": {"limit": 10000},
    "createdBy": "user@empresa.com"
  }'
```

---

## 📊 Datasets (datasetType)

```
0 = SoftwareInventory    (Software instalado)
1 = Logs                 (Eventos de sistema)
2 = ConfigurationAudit   (Alterações)
3 = Tickets              (Suporte)
4 = AgentHardware        (Hardware)
```

---

## 💾 Formatos (defaultFormat)

```
0 = Xlsx   (Excel)
1 = Csv    
2 = Pdf    (precisa estar habilitado na config)
```

---

## ✅ Validações Mínimas

| Campo | Tipo | Min | Max | Obrigatório |
|-------|------|-----|-----|-------------|
| **name** | string | 2 | 200 | ✅ SIM |
| **description** | string | 0 | 2000 | ❌ não |
| **datasetType** | enum | 0 | 4 | ✅ SIM |
| **defaultFormat** | enum | 0 | 2 | ✅ SIM |
| **layoutJson** | JSON | - | - | ✅ SIM |
| **filtersJson** | JSON | - | - | ❌ não |
| **createdBy** | string | 0 | 256 | ❌ não |

---

## 🔧 Layout JSON Mínimo

```json
{
  "layoutJson": {
    "title": "Título do Relatório",
    "columns": [
      {
        "field": "fieldName",
        "header": "Cabeçalho",
        "width": 20
      }
    ],
    "pageSize": 100,
    "orientation": "landscape"
  }
}
```

---

## 📝 Exemplo Completo

### Criar Template: Logs com Erros

```bash
curl -X POST http://localhost:5299/api/reports/templates \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Error Logs Last 7 Days",
    "description": "Critical and error level logs",
    "datasetType": 1,
    "defaultFormat": 0,
    "layoutJson": {
      "title": "Error Logs",
      "columns": [
        {"field": "timestamp", "header": "Date/Time", "width": 20, "format": "datetime"},
        {"field": "level", "header": "Level", "width": 10},
        {"field": "source", "header": "Source", "width": 25},
        {"field": "message", "header": "Message", "width": 50}
      ],
      "pageSize": 200,
      "orientation": "landscape"
    },
    "filtersJson": {
      "level": ["Error", "Critical"],
      "daysBack": 7,
      "limit": 10000,
      "orderBy": "timestamp",
      "orderDirection": "desc"
    },
    "createdBy": "admin@empresa.com"
  }'
```

**Resposta:**
```json
{
  "id": "507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3",
  "name": "Error Logs Last 7 Days",
  "version": 1,
  "isActive": true,
  "createdAt": "2026-03-08T14:22:00Z"
}
```

---

### Executar Template Criado

```bash
# Usar o ID retornado acima
curl -X POST http://localhost:5299/api/reports/run \
  -H "Content-Type: application/json" \
  -d '{
    "templateId": "507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3",
    "format": "Xlsx",
    "runAsync": true
  }'
```

**Resposta:**
```json
{
  "executionId": "019cceea-cb47-737b-977a-a47da8972606",
  "status": "Pending",
  "message": "Report execution queued for async processing."
}
```

---

### Verificar Progresso

```bash
curl http://localhost:5299/api/reports/executions/019cceea-cb47-737b-977a-a47da8972606
```

**Resposta:**
```json
{
  "id": "019cceea-cb47-737b-977a-a47da8972606",
  "status": "Completed",
  "resultSizeBytes": 45555,
  "rowCount": 150,
  "executionTimeMs": 3542
}
```

---

### Download Relatório

```bash
curl http://localhost:5299/api/reports/executions/019cceea-cb47-737b-977a-a47da8972606/download \
  -o error_logs.xlsx
```

---

## 🎨 Campos por Dataset

### SoftwareInventory (0)
```
clientId, siteId, agentId, softwareName, publisher, version,
installedAt, lastSeenAt, agentHostname, siteName
```

### Logs (1)
```
clientId, siteId, agentId, type, level, source, from, to,
createdAt, message, timestamp
```

### ConfigurationAudit (2)
```
entityType, entityId, fieldName, oldValue, newValue, changedBy,
changedAt, reason, timestamp
```

### Tickets (3)
```
clientId, siteId, agentId, workflowStateId, priority, createdAt,
closedAt, slaBreached, timestamp
```

### AgentHardware (4)
```
siteName, agentHostname, osName, osVersion, osBuild, osArchitecture,
processor, processorCores, processorThreads, processorArchitecture,
totalMemoryGB, motherboardManufacturer, motherboardModel,
biosVersion, biosManufacturer, collectedAt
```

---

## 🔄 Atualizar Template

```bash
curl -X PUT http://localhost:5299/api/reports/templates/507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3 \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Error Logs Updated",
    "description": "Now includes warning level",
    "filtersJson": {
      "level": ["Error", "Critical", "Warning"],
      "daysBack": 30,
      "limit": 10000
    },
    "updatedBy": "admin@empresa.com"
  }'
```

---

## 🗑️ Deletar Template

```bash
curl -X DELETE http://localhost:5299/api/reports/templates/507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3
```

---

## 📜 Ver Histórico de Versões

```bash
curl http://localhost:5299/api/reports/templates/507f1f77-bcf8-6f4f-b041-5d3b2c6a46c3/history?limit=10
```

**Resposta:**
```json
[
  {
    "version": 2,
    "eventType": "Updated",
    "createdAt": "2026-03-08T15:10:00Z",
    "createdBy": "admin@empresa.com"
  },
  {
    "version": 1,
    "eventType": "Created",
    "createdAt": "2026-03-08T14:22:00Z",
    "createdBy": "admin@empresa.com"
  }
]
```

---

## 🚨 Erros Comuns

| Erro | Causa | Solução |
|------|-------|---------|
| 400 Bad Request | JSON inválido | Verificar sintaxe |
| 400 Bad Request | name muito curto | Mínimo 2 caracteres |
| 400 Bad Request | datasetType inválido | Use 0-4 |
| 404 Not Found | Template não existe | Verificar ID |
| 422 Unprocessable | Validação falhou | Ver mensagem de erro |

---

## 🧠 Fluxo Completo

```
┌─ POST /templates
│  ├─ name, datasetType, layoutJson, filtersJson
│  └─ Resposta: id, version 1
│
├─ GET /templates/{id}
│  └─ Confirmar criação
│
├─ POST /run
│  ├─ templateId
│  └─ Resposta: executionId
│
├─ GET /executions/{id}
│  └─ Aguardar status = "Completed"
│
└─ GET /executions/{id}/download
   └─ Fazer download do arquivo
```

---

## 💡 Dicas

✅ **Use descrições claras** para que outros entendam o relatório
✅ **Sempre passe `createdBy`** para auditoria
✅ **Teste com pequeno `limit` primeiro** (ex: 100) antes de aumentar
✅ **Use `runAsync: true`** para relatórios grandes
✅ **Guarde o templateId** para reutilização
✅ **Consulte histórico** se precisar revert para versão anterior

---

## 📑 Documentação Completa

👉 Veja [MANUAL_REPORT_CREATION.md](MANUAL_REPORT_CREATION.md) para detalhes técnicos
👉 Veja [REPORT_CREATION_EXAMPLES.md](REPORT_CREATION_EXAMPLES.md) para exemplos em várias linguagens
