# 📋 Report Templates Seeding

## Overview

Os templates de relatorio agora são **cadastrados automaticamente** via migration (M034_SeedReportTemplates). 
Toda vez que rodar a API em um **servidor novo**, os 5 templates padrão serão criados automaticamente.

## Templates Criados

A migration M034 cria os seguintes templates:

### 1. **Software Inventory - All Clients**
- **Dataset Type**: SoftwareInventory
- **Default Format**: XLSX
- **Description**: Comprehensive list of installed software across all agents
- **Layout**: Columns para clientId, siteId, agentId, softwareName, publisher, version, installedAt
- **Filters**: Limit 5000, ordenado por installedAt DESC
- **Use Case**: Audit de sofwares licenciado, compliance

### 2. **System Logs - Last 7 Days**
- **Dataset Type**: Logs
- **Default Format**: XLSX
- **Description**: Recent system logs filtered by error and warning levels
- **Layout**: clientId, siteId, agentId, type, level, source, message, timestamp
- **Filters**: Apenas Error/Warning, últimos 7 dias, limit 10000
- **Use Case**: Troubleshooting, debugging, análise de eventos

### 3. **Configuration Changes - Monthly**
- **Dataset Type**: ConfigurationAudit
- **Default Format**: XLSX
- **Description**: Tracks all configuration modifications for compliance
- **Layout**: entityType, entityId, fieldName, oldValue, newValue, changedBy, changedAt, reason
- **Filters**: Últimos 30 dias, limit 10000
- **Use Case**: Change management, compliance, auditoria

### 4. **Open Tickets - Priority View**
- **Dataset Type**: Tickets
- **Default Format**: XLSX
- **Description**: Overview of open tickets sorted by priority and SLA status
- **Layout**: clientId, siteId, agentId, status, priority, createdAt, closedAt, slaBreached
- **Filters**: Status Open/InProgress, ordenado por priority ASC
- **Use Case**: Incident management, SLA tracking

### 5. **Agent Hardware Inventory**
- **Dataset Type**: AgentHardware
- **Default Format**: XLSX
- **Description**: Current hardware specifications of all agents
- **Layout**: clientId, siteId, agentId, osName, processor, totalMemoryGB, collectedAt
- **Filters**: Últimas coletas, limit 10000
- **Use Case**: Capacity planning, asset management

## Como Usar os Templates

### Ver Templates Disponíveis
```bash
GET /api/reports/templates
```

Resposta:
```json
[
  {
    "id": "992e5e0b-e2e0-4d2f-8d87-0119fa486afe",
    "name": "Software Inventory - All Clients",
    "datasetType": "SoftwareInventory",
    "defaultFormat": "Xlsx",
    "isActive": true,
    "createdBy": "migration"
  },
  ...
]
```

### Executar um Template
```bash
POST /api/reports/run
Content-Type: application/json

{
  "templateId": "992e5e0b-e2e0-4d2f-8d87-0119fa486afe",
  "clientId": "00000000-0000-0000-0000-000000000000",
  "format": "Xlsx",
  "runAsync": false  // true = queue, false = sync execution
}
```

Resposta (sync mode):
```json
{
  "executionId": "abc123...",
  "status": "Completed",
  "rowCount": 1234,
  "contentType": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  "resultSizeBytes": 245812,
  "downloadPath": "/api/reports/executions/abc123/download?clientId=..."
}
```

### Download do Relatório
```bash
GET /api/reports/executions/{executionId}/download?clientId={clientId}
```

## Customizar Templates

Os templates seeded são apenas **base de partida**. Você pode:

1. **Modificar via API**:
   ```bash
   PUT /api/reports/templates/{templateId}
   ```

2. **Criar novos templates**:
   ```bash
   POST /api/reports/templates
   ```

3. **Desativar templates**:
   ```bash
   PUT /api/reports/templates/{templateId}
   {
     "isActive": false
   }
   ```

## Deployment Notes

### First-Time Setup
1. Clonar repositório
2. Rodar `dotnet build`
3. Rodar a API (migrations rodam automaticamente)
4. Templates são criados automaticamente

### Staging/Production
1. Templates seeded já vêm prontos
2. Customizações feitas via API não são perdidas em re-deployments
3. Para resetan para template padrão: rodar `Rollback` seguido de `Migrate` novamente

## SQL Direto (Verificação)

Se necessário verificar templates via SQL:

```sql
SELECT id, name, dataset_type, created_by 
FROM report_templates 
WHERE created_by = 'migration'
ORDER BY created_at;
```

## Rollback

Se precisar desfazer o seeding (remove apenas templates com `created_by='migration'`):

Via FluentMigrator:
```bash
dotnet fm migrate -a Meduza.Migrations --down -v 34
```

Isso vai executar o `Down()` da migration M034, deletando apenas os templates seeded.

---

**Status**: ✅ Implementado em M034_SeedReportTemplates.cs  
**Auto-applied**: Sim (todos os novos servers recebem os 5 templates)  
**Rollback**: Seguro (deleta apenas seeded, não impacta customizações)
