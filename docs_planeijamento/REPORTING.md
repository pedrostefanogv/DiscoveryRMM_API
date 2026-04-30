# Reporting v2 — Optimization & Enhancement Plan

> **Status**: v1 implemented (MVP). v2 core infrastructure implemented.
> **Last updated**: 2026-04-29

---

## 0. What's New in v2 (Implemented 2026-04-29)

| Feature | Status | Details |
|---------|--------|---------|
| **Markdown renderer** | ✅ Done | `MarkdownReportRenderer` — GFM tables, grouped sections, summaries |
| **ReportFormat.Markdown** | ✅ Done | Enum value `3`, always enabled alongside XLSX/CSV |
| **Conditional formatting** | ✅ Model | `ReportLayoutConditionalFormat` in `ReportLayoutColumnDefinition` |
| **Charts layout** | ✅ Model | `ReportLayoutChartDefinition` with types: bar, pie, doughnut, line, gauge |
| **Computed fields** | ✅ Model | `ReportLayoutComputedFieldDefinition` |
| **PDF enhancements** | ✅ Model | Cover page, header/footer, TOC, watermark definitions |
| **Extended aggregates** | ✅ Done | avg, min, max in Markdown renderer (HTML pending) |
| **Report scheduling** | ✅ Done | CRUD endpoints + `ReportScheduleDispatchJob` (quartz every 60s) |
| **Template library** | ✅ Schema | `is_built_in` column + `M112` migration ready |
| **Rendering** | ⏳ Pending | Apply conditional formatting + charts to HTML/PDF renderers |
| **Built-in templates** | ⏳ Pending | Seed 17 templates via migration or startup |

---

## 1. Current State (v1)

### 1.1 Implemented Features

- 6 dataset types: `SoftwareInventory`, `Logs`, `ConfigurationAudit`, `Tickets`, `AgentHardware`, `AgentInventoryComposite`
- 3 output formats: PDF (Playwright → headless Chromium), XLSX (ClosedXML), CSV
- Multi-source layouts: `dataSources[]` with `alias.field` references + left/inner joins
- Layout features: `groupBy`, `groupDetails` (K/V cards), `sections` (subtables), `summaries` (count/countDistinct/sum), `style` customization
- Template CRUD with version history
- Preview API (HTML live preview + document download)
- Execution lifecycle: pending → running → completed/failed, with object storage persistence + presigned URLs
- Catalog + autocomplete + layout-schema discovery endpoints
- Background processing queue with configurable concurrency
- Notifications on completion/failure

### 1.2 Known Limitations

| Area | Limitation | Impact |
|------|-----------|--------|
| **Formats** | No Markdown renderer | Cannot export for docs/wiki/AI consumption |
| **PDF** | Flat HTML tables only | No page headers/footers, no cover page, no charts, no page numbers |
| **Visual** | No charts/graphs | Pure tabular data, no visual insights |
| **Templates** | No built-in library | Every MSP/user starts from zero |
| **Aggregates** | Only count/countDistinct/sum | No avg, min, max, percentiles, conditional |
| **Grouping** | Single-level only | Cannot nest groups (e.g. Client → Site → Agent) |
| **Scheduling** | Not implemented | No recurring report generation |
| **Computed fields** | Not supported | Cannot derive GB from bytes, calculate age, etc. |
| **Conditional formatting** | Not supported | No color-coded cells, no icon indicators |
| **Datasets** | Fixed enum | Not extensible without code changes |
| **Filters** | Manual JSON only | No parameterized/saved filter presets per user |
| **Charts** | None | No bar, pie, line, or gauge visualizations |

---

## 2. Optimization Plan (v2)

### 2.1 Phase 1 — Foundation & Markdown (sprint 1)

#### 2.1.1 Markdown Renderer
Novo `ReportFormat.Markdown` + `MarkdownReportRenderer : IReportRenderer`.

- Gera `.md` com tabelas GFM (GitHub Flavored Markdown)
- Suporta `groupBy` → seções com `## Título do Grupo`
- Suporta `summaries` → cards de resumo em markdown
- Suporta `sections` → subtabelas com `### Título da Seção`
- Útil para: integração com knowledge base, export para wiki, consumo por AI/LLM

**Exemplo de output Markdown:**
```markdown
# Inventário de Software — Cliente Acme Corp

| Hostname | Software | Versão | Publisher |
|----------|----------|--------|-----------|
| SRV-DC01 | Windows Server | 2022 | Microsoft |
| SRV-DC01 | SQL Server | 2019 | Microsoft |
| WS-001   | Chrome       | 120.0 | Google    |

---

**Resumo:** 45 softwares distintos em 12 agentes
```

#### 2.1.2 Template Library (Built-in)
Criar 15-20 templates pré-construídos acessíveis via `GET /api/reports/templates/library`:

| Categoria | Template | Dataset(s) | Uso típico |
|-----------|----------|------------|------------|
| **Inventory** | `hardware-inventory-full` | AgentHardware | Inventário completo de hardware |
| **Inventory** | `software-inventory-by-agent` | SoftwareInventory | Softwares instalados por máquina |
| **Inventory** | `machines-by-ram` | AgentHardware | Máquinas agrupadas por faixa de RAM |
| **Inventory** | `disk-usage-summary` | AgentHardware | Resumo de discos por agente |
| **Inventory** | `os-distribution` | AgentHardware | Distribuição de sistemas operacionais |
| **Inventory** | `agent-hardware-with-software` | AgentHardware + SoftwareInventory | Hardware + apps instalados (multi-source) |
| **Security** | `software-vulnerability-audit` | SoftwareInventory | Softwares desatualizados/EOF |
| **Security** | `agents-without-antivirus` | SoftwareInventory | Máquinas sem AV detectado |
| **Security** | `listening-ports-audit` | AgentHardware | Portas abertas por agente |
| **Compliance** | `configuration-audit-log` | ConfigurationAudit | Log de alterações de configuração |
| **Compliance** | `ticket-sla-report` | Tickets | Tickets com SLA breach |
| **Operations** | `agents-offline-report` | AgentHardware | Agentes offline/desatualizados |
| **Operations** | `ticket-summary-by-priority` | Tickets | Resumo de tickets por prioridade |
| **Operations** | `agent-labels-report` | AgentInventoryComposite | Agentes por label/tag |
| **Operations** | `system-logs-report` | Logs | Logs do sistema por período |
| **Billing** | `agent-count-by-client` | AgentHardware | Contagem de agentes por cliente |
| **Billing** | `software-license-count` | SoftwareInventory | Contagem de licenças de software |

#### 2.1.3 Computed / Derived Fields
Adicionar suporte a `computedFields` no layout JSON:

```json
{
  "computedFields": [
    { "name": "totalMemoryGB", "expression": "totalMemoryBytes / 1073741824", "format": "number" },
    { "name": "agentAgeDays", "expression": "now - firstSeenAt", "format": "number" },
    { "name": "slaStatus", "expression": "slaBreached ? 'VIOLATED' : 'OK'", "format": "text" }
  ]
}
```

### 2.2 Phase 2 — Rich PDF & Visualizations (sprint 2-3)

#### 2.2.1 PDF Enhancements — Cover Page
```json
{
  "coverPage": {
    "enabled": true,
    "title": "Relatório de Inventário",
    "subtitle": "Gerado em {{generatedAt}}",
    "logoUrl": "https://...",
    "showParameters": true,
    "showGeneratedAt": true,
    "showRowCount": true
  }
}
```

#### 2.2.2 PDF Enhancements — Page Header/Footer
```json
{
  "pageHeader": {
    "left": "Discovery RMM",
    "center": "{{reportTitle}}",
    "right": "{{currentDate}}"
  },
  "pageFooter": {
    "left": "Confidential",
    "center": "Page {{pageNumber}} of {{totalPages}}",
    "right": "{{clientName}}"
  }
}
```

#### 2.2.3 PDF Enhancements — Table of Contents
```json
{
  "tableOfContents": {
    "enabled": true,
    "title": "Índice",
    "maxLevel": 2
  }
}
```

#### 2.2.4 Charts & Visualizations
Novo nó `charts` no layout JSON para definir gráficos renderizados via QuickChart.io ou Chart.js server-side:

```json
{
  "charts": [
    {
      "type": "bar",
      "title": "Top 10 Softwares Instalados",
      "width": 800,
      "height": 400,
      "groupField": "softwareName",
      "valueField": "installedCount",
      "aggregate": "count",
      "limit": 10,
      "orientation": "horizontal"
    },
    {
      "type": "pie",
      "title": "Distribuição de SO",
      "width": 500,
      "height": 400,
      "groupField": "osName",
      "aggregate": "count"
    },
    {
      "type": "doughnut",
      "title": "Tickets por Prioridade",
      "width": 500,
      "height": 400,
      "groupField": "priority",
      "aggregate": "count"
    },
    {
      "type": "gauge",
      "title": "SLA Compliance",
      "width": 400,
      "height": 300,
      "value": 87.5,
      "min": 0,
      "max": 100,
      "thresholds": [
        { "value": 80, "color": "#ef4444" },
        { "value": 95, "color": "#f59e0b" },
        { "value": 100, "color": "#22c55e" }
      ]
    }
  ]
}
```

**Chart types suportados:**
- `bar` / `horizontalBar` — comparação categórica
- `line` — séries temporais
- `pie` / `doughnut` — proporções
- `stackedBar` — composição
- `gauge` — indicadores KPI (SLA, uso de disco, etc.)
- `sparkline` — mini-gráficos inline em células de tabela

#### 2.2.5 Conditional Formatting
```json
{
  "columns": [
    {
      "field": "slaBreached",
      "header": "SLA",
      "conditionalFormat": {
        "rules": [
          { "operator": "eq", "value": true, "backgroundColor": "#fee2e2", "textColor": "#dc2626", "icon": "⚠️" },
          { "operator": "eq", "value": false, "backgroundColor": "#dcfce7", "textColor": "#16a34a", "icon": "✓" }
        ]
      }
    },
    {
      "field": "totalMemoryBytes",
      "header": "RAM",
      "format": "bytes",
      "conditionalFormat": {
        "rules": [
          { "operator": "lt", "value": 4294967296, "backgroundColor": "#fef3c7", "icon": "⚠️" },
          { "operator": "gte", "value": 8589934592, "backgroundColor": "#dbeafe", "icon": "💪" }
        ]
      }
    }
  ]
}
```

#### 2.2.6 Watermarks
```json
{
  "watermark": {
    "text": "DRAFT",
    "color": "rgba(0,0,0,0.06)",
    "fontSize": 120,
    "angle": -45,
    "repeat": true
  }
}
```

### 2.3 Phase 3 — Advanced Queries & Datasets (sprint 3-4)

#### 2.3.1 New Dataset Types
| DatasetType | Fontes de dados | Campos principais |
|-------------|----------------|-------------------|
| `AgentPerformance` | `p2p_agent_telemetry` | cpuPercent, ramPercent, diskPercent, networkKbps, collectedAt |
| `TicketSLA` | `tickets` (agregado) | totalTickets, breachedCount, compliancePercent, avgResolutionHours |
| `SoftwareCompliance` | `software_catalog` + `agent_software_inventory` | licenseName, requiredCount, installedCount, complianceStatus |
| `AgentUptime` | `agents` + logs | agentId, lastSeenAt, uptimePercent7d, uptimePercent30d |
| `DeploymentStatus` | `automation_task_audits` | taskName, agentId, status, startedAt, completedAt, exitCode |
| `AppStoreUsage` | `app_packages` + `automation_execution_reports` | packageName, installCount, lastInstalledAt |

#### 2.3.2 Nested Grouping (Multi-Level)
```json
{
  "groupBy": ["clientName", "siteName", "agentHostname"],
  "groupLevels": [
    { "field": "clientName", "titleTemplate": "Cliente: {{value}}", "pageBreak": true },
    { "field": "siteName", "titleTemplate": "Site: {{value}}" },
    { "field": "agentHostname", "titleTemplate": "Agente: {{value}}" }
  ]
}
```

#### 2.3.3 Extended Aggregates
Adicionar: `avg`, `min`, `max`, `median`, `percentile75`, `percentile90`, `countIf`, `sumIf`, `first`, `last`.

#### 2.3.4 Parameterized Filter Presets
Permitir salvar filtros por usuário:
```
POST /api/reports/filter-presets
{
  "name": "Meus agentes Windows",
  "templateId": "...",
  "filters": { "osName": "Windows", "clientId": "..." }
}
```

### 2.4 Phase 4 — Scheduling & Distribution (sprint 4-5)

#### 2.4.1 Report Scheduling
```
POST /api/reports/schedules
{
  "templateId": "...",
  "format": "pdf",
  "schedule": "0 8 * * 1",        // cron: toda segunda 08:00
  "filters": { "clientId": "..." },
  "recipients": ["user1@email.com", "user2@email.com"],
  "deliveryMode": "email"          // email | webhook | storage
}
```

#### 2.4.2 Email Delivery
Integrar com o sistema de notificações + SMTP para enviar reports por email como anexo.

---

## 3. Rich Report Examples (Modelos)

Abaixo, exemplos concretos de layouts JSON e dados esperados para cenários reais de RMM.

### 3.1 Máquinas com +8GB RAM + Aplicativos Instalados

**Dataset**: Multi-source (`AgentHardware` + `SoftwareInventory`)

```json
{
  "title": "Máquinas com +8GB RAM e Aplicativos Instalados",
  "subtitle": "Gerado em {{generatedAt}} — Filtro: RAM > 8GB",
  "orientation": "landscape",
  "dataSources": [
    {
      "datasetType": "AgentHardware",
      "alias": "hw",
      "filters": {
        "minTotalMemoryBytes": 8589934592
      }
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
  "coverPage": {
    "enabled": true,
    "title": "Relatório: Máquinas com +8GB RAM",
    "subtitle": "Inventário de hardware + software instalado",
    "showParameters": true,
    "logoUrl": null
  },
  "pageHeader": {
    "left": "Discovery RMM",
    "center": "{{reportTitle}}",
    "right": "{{currentDate}}"
  },
  "pageFooter": {
    "left": "Confidencial",
    "center": "Página {{pageNumber}} de {{totalPages}}",
    "right": "{{clientName}}"
  },
  "groupBy": "hw.agentHostname",
  "groupTitleTemplate": "🖥️ {{value}}",
  "hideGroupColumn": true,
  "groupDetails": [
    { "field": "hw.osName", "label": "Sistema Operacional" },
    { "field": "hw.totalMemoryBytes", "label": "RAM Total", "format": "bytes" },
    { "field": "hw.processor", "label": "Processador" },
    { "field": "hw.processorCores", "label": "Núcleos" },
    { "field": "hw.gpuModel", "label": "GPU" },
    { "field": "hw.totalDisksCount", "label": "Discos" }
  ],
  "groupSummaries": [
    { "label": "Total de Apps", "field": "sw.softwareName", "aggregate": "countDistinct" }
  ],
  "sections": [
    {
      "title": "📦 Aplicativos Instalados",
      "columns": [
        { "field": "sw.softwareName", "header": "Software" },
        { "field": "sw.publisher", "header": "Fabricante" },
        { "field": "sw.version", "header": "Versão" },
        { "field": "sw.lastSeenAt", "header": "Última Verificação", "format": "datetime" }
      ]
    }
  ],
  "charts": [
    {
      "type": "horizontalBar",
      "title": "Top 10 Aplicativos Mais Comuns",
      "groupField": "sw.softwareName",
      "aggregate": "count",
      "limit": 10
    },
    {
      "type": "pie",
      "title": "Distribuição de SO",
      "groupField": "hw.osName",
      "aggregate": "count"
    }
  ],
  "summaries": [
    { "label": "Total de Máquinas", "aggregate": "count" },
    { "label": "RAM Média (GB)", "field": "hw.totalMemoryBytes", "aggregate": "avg" },
    { "label": "Softwares Distintos", "field": "sw.softwareName", "aggregate": "countDistinct" }
  ],
  "style": {
    "primaryColor": "#0f4c81",
    "headerBackgroundColor": "#1e40af",
    "headerTextColor": "#ffffff",
    "alternateRowColor": "#f0f4ff",
    "fontFamily": "Inter, Segoe UI, Arial, sans-serif"
  }
}
```

### 3.2 Relatório de SLA de Tickets (Compliance)

**Dataset**: `Tickets`

```json
{
  "title": "Relatório de SLA — Tickets",
  "subtitle": "Período: últimos 30 dias",
  "orientation": "portrait",
  "groupBy": "priority",
  "groupTitleTemplate": "Prioridade: {{value}}",
  "groupDetails": [
    { "field": "priority", "label": "Prioridade" }
  ],
  "groupSummaries": [
    { "label": "Total", "aggregate": "count" },
    { "label": "SLA Violados", "field": "slaBreached", "aggregate": "countIf", "condition": { "eq": true } },
    { "label": "Compliance", "field": "slaBreached", "aggregate": "compliancePercent" }
  ],
  "columns": [
    { "field": "id", "header": "Ticket #" },
    { "field": "title", "header": "Título" },
    { "field": "priority", "header": "Prioridade" },
    { "field": "createdAt", "header": "Abertura", "format": "datetime" },
    { "field": "slaExpiresAt", "header": "SLA Expira", "format": "datetime" },
    {
      "field": "slaBreached",
      "header": "Status SLA",
      "conditionalFormat": {
        "rules": [
          { "operator": "eq", "value": true, "backgroundColor": "#fee2e2", "textColor": "#dc2626", "label": "VIOLADO" },
          { "operator": "eq", "value": false, "backgroundColor": "#dcfce7", "textColor": "#16a34a", "label": "OK" }
        ]
      }
    },
    { "field": "closedAt", "header": "Fechamento", "format": "datetime" }
  ],
  "charts": [
    {
      "type": "gauge",
      "title": "SLA Compliance Geral",
      "valueExpr": "compliancePercent",
      "thresholds": [
        { "value": 80, "color": "#ef4444" },
        { "value": 95, "color": "#f59e0b" },
        { "value": 100, "color": "#22c55e" }
      ]
    },
    {
      "type": "doughnut",
      "title": "Tickets por Prioridade",
      "groupField": "priority",
      "aggregate": "count"
    }
  ],
  "summaries": [
    { "label": "Total Tickets", "aggregate": "count" },
    { "label": "SLA Compliance", "field": "slaBreached", "aggregate": "compliancePercent", "format": "percent" },
    { "label": "Tempo Médio Resolução (h)", "field": "resolutionHours", "aggregate": "avg" }
  ]
}
```

### 3.3 Inventário de Hardware Completo (por Cliente)

**Dataset**: `AgentHardware`

```json
{
  "title": "Inventário Completo de Hardware",
  "subtitle": "Agrupado por Cliente → Site",
  "orientation": "landscape",
  "groupBy": "clientName",
  "groupTitleTemplate": "🏢 {{value}}",
  "groupDetails": [
    { "field": "clientName", "label": "Cliente" }
  ],
  "groupSummaries": [
    { "label": "Agentes", "aggregate": "count" },
    { "label": "RAM Total (GB)", "field": "totalMemoryBytes", "aggregate": "sum" },
    { "label": "Núcleos Totais", "field": "processorCores", "aggregate": "sum" }
  ],
  "columns": [
    { "field": "siteName", "header": "Site" },
    { "field": "agentHostname", "header": "Hostname" },
    { "field": "osName", "header": "Sistema Operacional" },
    { "field": "osVersion", "header": "Versão OS" },
    { "field": "osArchitecture", "header": "Arquitetura" },
    { "field": "manufacturer", "header": "Fabricante" },
    { "field": "model", "header": "Modelo" },
    { "field": "processor", "header": "Processador" },
    { "field": "processorCores", "header": "Núcleos" },
    { "field": "processorThreads", "header": "Threads" },
    { "field": "processorFrequencyGhz", "header": "Freq. (GHz)", "format": "number" },
    { "field": "totalMemoryBytes", "header": "RAM", "format": "bytes" },
    { "field": "gpuModel", "header": "GPU" },
    { "field": "totalDisksCount", "header": "Discos" },
    { "field": "biosVersion", "header": "BIOS" },
    { "field": "collectedAt", "header": "Coleta", "format": "datetime" }
  ],
  "charts": [
    {
      "type": "bar",
      "title": "Agentes por Cliente",
      "groupField": "clientName",
      "aggregate": "count"
    },
    {
      "type": "pie",
      "title": "Distribuição de SO",
      "groupField": "osName",
      "aggregate": "count"
    },
    {
      "type": "horizontalBar",
      "title": "Top Fabricantes",
      "groupField": "manufacturer",
      "aggregate": "count",
      "limit": 8
    }
  ],
  "summaries": [
    { "label": "Total de Agentes", "aggregate": "count" },
    { "label": "RAM Total (TB)", "field": "totalMemoryBytes", "aggregate": "sum" },
    { "label": "Núcleos Totais", "field": "processorCores", "aggregate": "sum" },
    { "label": "Média RAM/Agente (GB)", "field": "totalMemoryBytes", "aggregate": "avg" }
  ]
}
```

### 3.4 Auditoria de Segurança — Softwares Vulneráveis

**Dataset**: `SoftwareInventory`

```json
{
  "title": "Auditoria de Software — Segurança",
  "subtitle": "Softwares potencialmente desatualizados ou inseguros",
  "orientation": "portrait",
  "filters": {
    "softwareName": "Java|Flash|Adobe Reader|WinRAR|7-Zip|VNC|TeamViewer|Putty|Notepad++"
  },
  "groupBy": "agentHostname",
  "groupTitleTemplate": "⚠️ {{value}}",
  "groupDetails": [
    { "field": "agentHostname", "label": "Hostname" },
    { "field": "clientName", "label": "Cliente" },
    { "field": "siteName", "label": "Site" },
    { "field": "osName", "label": "SO" }
  ],
  "columns": [
    { "field": "softwareName", "header": "Software" },
    { "field": "publisher", "header": "Fabricante" },
    { "field": "version", "header": "Versão Instalada" },
    { "field": "lastSeenAt", "header": "Última Verificação", "format": "datetime" }
  ],
  "summaries": [
    { "label": "Agentes Afetados", "field": "agentHostname", "aggregate": "countDistinct" },
    { "label": "Softwares Distintos", "field": "softwareName", "aggregate": "countDistinct" }
  ],
  "charts": [
    {
      "type": "horizontalBar",
      "title": "Softwares Mais Encontrados",
      "groupField": "softwareName",
      "aggregate": "count",
      "limit": 15
    }
  ],
  "style": {
    "primaryColor": "#b91c1c",
    "headerBackgroundColor": "#dc2626",
    "headerTextColor": "#ffffff",
    "alternateRowColor": "#fef2f2"
  }
}
```

### 3.5 Relatório de Logs do Sistema

**Dataset**: `Logs`

```json
{
  "title": "Logs do Sistema",
  "subtitle": "Últimas 24h — Nível Warning ou superior",
  "orientation": "landscape",
  "filters": {
    "from": "{{now-24h}}",
    "to": "{{now}}"
  },
  "groupBy": "level",
  "groupTitleTemplate": "{{value}}",
  "columns": [
    { "field": "createdAt", "header": "Data/Hora", "format": "datetime" },
    { "field": "level", "header": "Nível",
      "conditionalFormat": {
        "rules": [
          { "operator": "eq", "value": "Error", "backgroundColor": "#fee2e2", "textColor": "#dc2626" },
          { "operator": "eq", "value": "Warning", "backgroundColor": "#fef3c7", "textColor": "#d97706" },
          { "operator": "eq", "value": "Information", "backgroundColor": "#dbeafe", "textColor": "#2563eb" }
        ]
      }
    },
    { "field": "source", "header": "Origem" },
    { "field": "type", "header": "Tipo" },
    { "field": "message", "header": "Mensagem" },
    { "field": "agentId", "header": "Agente" }
  ],
  "charts": [
    {
      "type": "line",
      "title": "Logs por Hora (últimas 24h)",
      "groupField": "createdAt",
      "aggregate": "count",
      "bucketBy": "hour"
    },
    {
      "type": "doughnut",
      "title": "Distribuição por Nível",
      "groupField": "level",
      "aggregate": "count"
    },
    {
      "type": "doughnut",
      "title": "Distribuição por Origem",
      "groupField": "source",
      "aggregate": "count"
    }
  ],
  "summaries": [
    { "label": "Total de Logs", "aggregate": "count" },
    { "label": "Erros", "field": "level", "aggregate": "countIf", "condition": { "eq": "Error" } },
    { "label": "Warnings", "field": "level", "aggregate": "countIf", "condition": { "eq": "Warning" } }
  ]
}
```

### 3.6 Distribuição de SO (Executive Summary)

**Dataset**: `AgentHardware`

```json
{
  "title": "Resumo Executivo — Parque de Máquinas",
  "subtitle": "{{clientName}} — {{currentDate}}",
  "orientation": "portrait",
  "coverPage": {
    "enabled": true,
    "title": "Resumo Executivo",
    "subtitle": "Parque de Máquinas — {{clientName}}"
  },
  "tableOfContents": { "enabled": true },
  "sections": [
    {
      "title": "📊 Distribuição por Sistema Operacional",
      "columns": [
        { "field": "osName", "header": "Sistema Operacional" },
        { "field": "osVersion", "header": "Versão" },
        { "field": "osArchitecture", "header": "Arquitetura" }
      ]
    },
    {
      "title": "🖥️ Resumo de Hardware",
      "columns": [
        { "field": "agentHostname", "header": "Hostname" },
        { "field": "siteName", "header": "Site" },
        { "field": "manufacturer", "header": "Fabricante" },
        { "field": "model", "header": "Modelo" },
        { "field": "processor", "header": "Processador" },
        { "field": "totalMemoryBytes", "header": "RAM", "format": "bytes" },
        { "field": "totalDisksCount", "header": "Discos" }
      ]
    }
  ],
  "charts": [
    {
      "type": "doughnut",
      "title": "Distribuição de Sistemas Operacionais",
      "groupField": "osName",
      "aggregate": "count"
    },
    {
      "type": "horizontalBar",
      "title": "Máquinas por Site",
      "groupField": "siteName",
      "aggregate": "count"
    },
    {
      "type": "bar",
      "title": "RAM Total por Site (GB)",
      "groupField": "siteName",
      "valueField": "totalMemoryBytes",
      "aggregate": "sum"
    }
  ],
  "summaries": [
    { "label": "Total de Máquinas", "aggregate": "count" },
    { "label": "Sites", "field": "siteName", "aggregate": "countDistinct" },
    { "label": "RAM Total (GB)", "field": "totalMemoryBytes", "aggregate": "sum" },
    { "label": "Núcleos Totais", "field": "processorCores", "aggregate": "sum" }
  ],
  "style": {
    "primaryColor": "#0f4c81",
    "headerBackgroundColor": "#1e3a5f",
    "fontFamily": "Segoe UI, Arial, sans-serif"
  }
}
```

### 3.7 Rastreamento de Alterações (Configuration Audit)

**Dataset**: `ConfigurationAudit`

```json
{
  "title": "Registro de Alterações de Configuração",
  "subtitle": "Últimos 7 dias",
  "orientation": "landscape",
  "groupBy": "entityType",
  "groupTitleTemplate": "📝 {{value}}",
  "columns": [
    { "field": "changedAt", "header": "Data/Hora", "format": "datetime" },
    { "field": "changedBy", "header": "Usuário" },
    { "field": "entityType", "header": "Tipo Entidade" },
    { "field": "entityId", "header": "ID Entidade" },
    { "field": "fieldName", "header": "Campo" },
    { "field": "oldValue", "header": "Valor Anterior" },
    { "field": "newValue", "header": "Novo Valor" },
    { "field": "reason", "header": "Motivo" }
  ],
  "charts": [
    {
      "type": "bar",
      "title": "Alterações por Dia",
      "groupField": "changedAt",
      "aggregate": "count",
      "bucketBy": "day"
    },
    {
      "type": "doughnut",
      "title": "Alterações por Tipo de Entidade",
      "groupField": "entityType",
      "aggregate": "count"
    }
  ],
  "summaries": [
    { "label": "Total de Alterações", "aggregate": "count" },
    { "label": "Usuários Distintos", "field": "changedBy", "aggregate": "countDistinct" }
  ]
}
```

### 3.8 Agentes Offline / Sem Comunicação

**Dataset**: `AgentInventoryComposite`

```json
{
  "title": "Agentes Sem Comunicação",
  "subtitle": "Agentes offline há mais de 24h",
  "orientation": "portrait",
  "filters": {
    "status": "offline",
    "offlineSinceHours": 24
  },
  "groupBy": "clientName",
  "groupTitleTemplate": "🏢 {{value}}",
  "columns": [
    { "field": "agentHostname", "header": "Hostname" },
    { "field": "siteName", "header": "Site" },
    { "field": "lastSeenAt", "header": "Último Contato", "format": "datetime" },
    { "field": "osName", "header": "SO" },
    { "field": "agentVersion", "header": "Versão Agent" },
    { "field": "lastIpAddress", "header": "Último IP" }
  ],
  "summaries": [
    { "label": "Total Offline", "aggregate": "count" },
    { "label": "Clientes Afetados", "field": "clientName", "aggregate": "countDistinct" }
  ],
  "style": {
    "primaryColor": "#b91c1c",
    "headerBackgroundColor": "#dc2626",
    "headerTextColor": "#ffffff"
  }
}
```

---

## 4. New API Endpoints (v2)

| Area | Method | Endpoint | Description |
|------|--------|----------|-------------|
| **Library** | `GET` | `/api/reports/templates/library` | Listar templates pré-construídos |
| **Library** | `POST` | `/api/reports/templates/library/{id}/install` | Instalar template da biblioteca |
| **Library** | `GET` | `/api/reports/templates/library/{id}/preview` | Preview de template da biblioteca |
| **Schedules** | `POST` | `/api/reports/schedules` | Criar agendamento |
| **Schedules** | `GET` | `/api/reports/schedules` | Listar agendamentos |
| **Schedules** | `PUT` | `/api/reports/schedules/{id}` | Atualizar agendamento |
| **Schedules** | `DELETE` | `/api/reports/schedules/{id}` | Remover agendamento |
| **Presets** | `POST` | `/api/reports/filter-presets` | Salvar preset de filtro |
| **Presets** | `GET` | `/api/reports/filter-presets` | Listar presets do usuário |
| **Presets** | `DELETE` | `/api/reports/filter-presets/{id}` | Remover preset |
| **Export** | `POST` | `/api/reports/templates/{id}/export` | Exportar template como JSON |
| **Import** | `POST` | `/api/reports/templates/import` | Importar template de JSON |
| **Charts** | `GET` | `/api/reports/charts/preview` | Preview de gráfico isolado |

---

## 5. Implementation Roadmap

```
Sprint 1 (Foundation)
├── Markdown renderer (IMarkdownReportRenderer)
├── Template library (built-in templates with install endpoint)
├── Computed/derived fields in layout JSON
└── Extended aggregates (avg, min, max, countIf, sumIf)

Sprint 2 (Rich PDF)
├── Cover page with logo + metadata
├── Page header/footer with page numbers
├── Table of Contents generation
├── Watermark support
└── Conditional formatting in all renderers

Sprint 3 (Visualizations)
├── Chart engine (QuickChart.io or SkiaSharp server-side)
├── Bar, pie, doughnut, line, horizontalBar charts
├── Gauge charts for KPIs
├── Sparklines in table cells
└── Chart configuration in layout JSON

Sprint 4 (Advanced Data)
├── New datasets: AgentPerformance, TicketSLA, SoftwareCompliance, AgentUptime
├── Nested multi-level grouping
├── Date bucketing (hour/day/week/month/year)
├── Pivot/cross-tab layout support
└── Parameterized filter presets API

Sprint 5 (Scheduling & Distribution)
├── Report scheduling CRUD with cron expressions
├── Background scheduler service (Quartz.NET or similar)
├── Email delivery integration
├── Webhook delivery
└── Schedule history and monitoring
```

---

## 6. Architecture Decisions

### 6.1 Chart Rendering: QuickChart.io vs Server-Side
- **Recomendação**: QuickChart.io para MVPs, migrar para SkiaSharp server-side para produção
- QuickChart: Sem dependências, URL-based, caching-friendly
- SkiaSharp: Self-hosted, zero latência externa, ilimitado

### 6.2 Markdown: String Builder vs Template Engine
- **Recomendação**: Manter consistência com `ReportHtmlComposer` — interpolação manual de string via StringBuilder
- Gera GFM (GitHub Flavored Markdown) com tabelas alinhadas

### 6.3 Scheduling: Quartz.NET vs Built-in Timer
- **Recomendação**: `ReportGenerationBackgroundService` já existe como `BackgroundService`. Estender para scheduling com `PeriodicTimer` + cron parsing (`Cronos` library)

### 6.4 Chart Library: QuickChart.io (MVP) → SkiaSharp + Chart library (produção)
- QuickChart: `GET https://quickchart.io/chart?c={json}` → retorna PNG
- Produção: usar `SkiaSharp` para renderizar charts server-side sem dependência externa

---

## 7. Related Docs

- `CONFIGURATION.md` for reporting settings
- `OBJECT_STORAGE.md` for download/storage behavior
- `AUTHENTICATION.md` for API auth on new endpoints
- `ADR_BACKGROUND_JOBS.md` for scheduling architecture
