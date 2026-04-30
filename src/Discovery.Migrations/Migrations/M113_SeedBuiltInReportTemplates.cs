using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Seeds 17 built-in report templates into report_templates.
/// Each template uses gen_random_uuid() — idempotent per name + is_built_in.
/// These templates serve as starting points for MSPs/users and can be cloned/installed.
/// </summary>
[Migration(20260429_113)]
public class M113_SeedBuiltInReportTemplates : Migration
{    public override void Up()
    {
        InsertTemplate(0, "Inventário Completo de Hardware",
            "Inventário completo de hardware por agente: SO, CPU, RAM, GPU, discos, BIOS.",
            null, 4, "Pdf", 4, GetHardwareInventoryLayout(), null);

        InsertTemplate(1, "Inventário de Software por Máquina",
            "Softwares instalados por máquina: nome, versão, fabricante, última verificação.",
            null, 0, "Xlsx", 0, GetSoftwareByAgentLayout(), null);

        InsertTemplate(2, "Máquinas por Faixa de RAM",
            "Agentes agrupados por capacidade de RAM (4GB-, 4-8GB, 8-16GB, 16-32GB, 32GB+).",
            null, 4, "Pdf", 4, GetMachinesByRamLayout(), null);

        InsertTemplate(3, "Resumo de Uso de Discos",
            "Resumo de discos por agente: quantidade, espaço total e livre.",
            null, 4, "Xlsx", 4, GetDiskSummaryLayout(), null);

        InsertTemplate(4, "Distribuição de Sistemas Operacionais",
            "Distribuição de sistemas operacionais no parque (Windows 10, 11, Server, Linux).",
            null, 4, "Pdf", 5, GetOsDistributionLayout(), null);

        InsertTemplate(5, "Hardware + Aplicativos Instalados",
            "Multi-source: hardware do agente com todos os softwares instalados correlacionados.",
            null, 5, "Pdf", 5, GetHardwareWithSoftwareLayout(), null);

        InsertTemplate(6, "Auditoria de Softwares Vulneráveis",
            "Softwares potencialmente inseguros ou desatualizados (Java, Flash, VNC, TeamViewer, etc).",
            null, 0, "Pdf", 0, GetVulnerabilityAuditLayout(), "{\"softwareName\":\"Java|Flash|Adobe|VNC|TeamViewer|Putty|WinRAR|7-Zip|Notepad++\"}");

        InsertTemplate(7, "Máquinas sem Antivírus Detectado",
            "Agentes sem software de antivírus identificado no inventário.",
            null, 0, "Xlsx", 0, GetAgentsWithoutAVLayout(), "{\"softwareName\":\"Defender|Avast|AVG|McAfee|Norton|Kaspersky|Bitdefender|ESET|Sophos|SentinelOne|CrowdStrike|Carbon Black|Trend Micro|Malwarebytes\"}");

        InsertTemplate(8, "Registro de Alterações de Configuração",
            "Log de auditoria de alterações de configuração no sistema.",
            null, 2, "Xlsx", 2, GetConfigAuditLayout(), null);

        InsertTemplate(9, "Relatório de SLA de Tickets",
            "Tickets com métricas de SLA: compliance, violações, tempo médio de resolução.",
            null, 3, "Pdf", 3, GetTicketSLALayout(), null);

        InsertTemplate(10, "Tickets por Prioridade",
            "Resumo de tickets agrupados por prioridade com contagem e SLA status.",
            null, 3, "Pdf", 3, GetTicketsByPriorityLayout(), null);

        InsertTemplate(11, "Agentes Sem Comunicação",
            "Agentes offline há mais de 24h, agrupados por cliente.",
            null, 5, "Pdf", 5, GetOfflineAgentsLayout(), null);

        InsertTemplate(12, "Agentes por Label/Tag",
            "Agentes agrupados por labels automáticas configuradas.",
            null, 5, "Xlsx", 5, GetAgentsByLabelLayout(), null);

        InsertTemplate(13, "Logs do Sistema (24h)",
            "Logs do sistema das últimas 24h com distribuição por nível e origem.",
            null, 1, "Xlsx", 1, GetSystemLogsLayout(), null);

        InsertTemplate(14, "Contagem de Agentes por Cliente",
            "Resumo executivo: total de agentes e RAM por cliente.",
            null, 4, "Pdf", 4, GetAgentCountByClientLayout(), null);

        InsertTemplate(15, "Contagem de Licenças de Software",
            "Software inventory agregado: quantas instalações de cada software.",
            null, 0, "Xlsx", 0, GetSoftwareLicenseCountLayout(), null);

        InsertTemplate(16, "Resumo Executivo do Parque",
            "Visão executiva consolidada: máquinas, SO, RAM total, núcleos por cliente/site.",
            null, 4, "Pdf", 4, GetExecutiveSummaryLayout(), null);
    }

    public override void Down()
    {
        Execute.Sql("DELETE FROM report_templates WHERE is_built_in = true AND created_by = 'system';");
    }

    private void InsertTemplate(int index, string name, string description,
        string? filtersJson, int datasetType, string defaultFormat, int datasetTypeForSchema,
        string layoutJson, string? templateFiltersJson)
    {
        var now = DateTime.UtcNow;

        Insert.IntoTable("report_templates")
            .Row(new
            {
                id = Guid.NewGuid(),
                client_id = (Guid?)null,
                name,
                description,
                instructions = (string?)null,
                execution_schema_json = (string?)null,
                dataset_type = datasetType,
                default_format = defaultFormat == "Pdf" ? 1 : defaultFormat == "Csv" ? 2 : defaultFormat == "Markdown" ? 3 : 0,
                layout_json = layoutJson,
                filters_json = templateFiltersJson ?? (object)DBNull.Value,
                is_active = true,
                is_built_in = true,
                version = 1,
                created_at = now,
                updated_at = now,
                created_by = "system",
                updated_by = "system"
            });
    }

    // ═══════════════════════════════════════════════════════════════
    // Layout JSONs for each template (simplified)
    // ═══════════════════════════════════════════════════════════════

    private static string GetHardwareInventoryLayout() => /*lang=json*/ """
        {
            "title": "Inventário de Hardware",
            "subtitle": "Gerado em {{generatedAt}}",
            "orientation": "landscape",
            "groupBy": "clientName",
            "groupTitleTemplate": "🏢 {{value}}",
            "hideGroupColumn": true,
            "groupDetails": [
                { "field": "clientName", "label": "Cliente" }
            ],
            "groupSummaries": [
                { "label": "Agentes", "aggregate": "count" },
                { "label": "RAM Total (GB)", "field": "totalMemoryBytes", "aggregate": "sum", "format": "bytes" }
            ],
            "columns": [
                { "field": "siteName", "header": "Site" },
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "osName", "header": "SO" },
                { "field": "osVersion", "header": "Versão OS" },
                { "field": "manufacturer", "header": "Fabricante" },
                { "field": "model", "header": "Modelo" },
                { "field": "processor", "header": "Processador" },
                { "field": "processorCores", "header": "Núcleos" },
                { "field": "totalMemoryBytes", "header": "RAM", "format": "bytes" },
                { "field": "gpuModel", "header": "GPU" },
                { "field": "totalDisksCount", "header": "Discos" },
                { "field": "collectedAt", "header": "Coleta", "format": "datetime" }
            ],
            "charts": [
                { "type": "pie", "title": "Distribuição de SO", "groupField": "osName", "aggregate": "count" },
                { "type": "horizontalBar", "title": "Top Fabricantes", "groupField": "manufacturer", "aggregate": "count", "limit": 8 }
            ],
            "summaries": [
                { "label": "Total de Agentes", "aggregate": "count" },
                { "label": "RAM Total (GB)", "field": "totalMemoryBytes", "aggregate": "sum", "format": "bytes" },
                { "label": "Núcleos Totais", "field": "processorCores", "aggregate": "sum" }
            ],
            "style": {
                "primaryColor": "#0f4c81",
                "headerBackgroundColor": "#1e3a5f",
                "alternateRowColor": "#f0f4ff"
            }
        }
        """;

    private static string GetSoftwareByAgentLayout() => /*lang=json*/ """
        {
            "title": "Inventário de Software por Máquina",
            "subtitle": "Agrupado por hostname",
            "orientation": "landscape",
            "groupBy": "agentHostname",
            "groupTitleTemplate": "🖥️ {{value}}",
            "hideGroupColumn": true,
            "groupDetails": [
                { "field": "agentHostname", "label": "Hostname" },
                { "field": "siteName", "label": "Site" },
                { "field": "clientName", "label": "Cliente" }
            ],
            "groupSummaries": [
                { "label": "Total de Apps", "field": "softwareName", "aggregate": "countDistinct" }
            ],
            "columns": [
                { "field": "softwareName", "header": "Software" },
                { "field": "publisher", "header": "Fabricante" },
                { "field": "version", "header": "Versão" },
                { "field": "lastSeenAt", "header": "Última Verificação", "format": "datetime" }
            ],
            "summaries": [
                { "label": "Softwares Distintos", "field": "softwareName", "aggregate": "countDistinct" },
                { "label": "Total Instalações", "aggregate": "count" }
            ],
            "style": {
                "primaryColor": "#0f4c81",
                "alternateRowColor": "#f0f4ff"
            }
        }
        """;

    private static string GetMachinesByRamLayout() => /*lang=json*/ """
        {
            "title": "Máquinas por Faixa de RAM",
            "subtitle": "Agrupado por capacidade de memória",
            "orientation": "portrait",
            "groupBy": "totalMemoryBytes",
            "groupTitleTemplate": "{{value}}",
            "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "clientName", "header": "Cliente" },
                { "field": "siteName", "header": "Site" },
                { "field": "osName", "header": "SO" },
                { "field": "processor", "header": "Processador" },
                { "field": "totalMemoryBytes", "header": "RAM", "format": "bytes" },
                { "field": "gpuModel", "header": "GPU" }
            ],
            "charts": [
                { "type": "bar", "title": "Máquinas por Faixa de RAM", "groupField": "totalMemoryBytes", "aggregate": "count" }
            ],
            "summaries": [
                { "label": "Total de Máquinas", "aggregate": "count" },
                { "label": "RAM Média (GB)", "field": "totalMemoryBytes", "aggregate": "avg", "format": "bytes" },
                { "label": "RAM Total (GB)", "field": "totalMemoryBytes", "aggregate": "sum", "format": "bytes" }
            ],
            "style": {
                "primaryColor": "#0f4c81"
            }
        }
        """;

    private static string GetDiskSummaryLayout() => /*lang=json*/ """
        {
            "title": "Resumo de Discos",
            "subtitle": "Por agente",
            "orientation": "portrait",
            "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "clientName", "header": "Cliente" },
                { "field": "siteName", "header": "Site" },
                { "field": "totalDisksCount", "header": "Qtd. Discos" },
                { "field": "osName", "header": "SO" }
            ],
            "summaries": [
                { "label": "Total de Agentes", "aggregate": "count" },
                { "label": "Média Discos/Agente", "field": "totalDisksCount", "aggregate": "avg" }
            ]
        }
        """;

    private static string GetOsDistributionLayout() => /*lang=json*/ """
        {
            "title": "Distribuição de Sistemas Operacionais",
            "subtitle": "Visão geral do parque",
            "orientation": "portrait",
            "coverPage": { "enabled": true, "title": "Distribuição de SO", "showGeneratedAt": true, "showRowCount": true },
            "groupBy": "osName",
            "groupTitleTemplate": "💻 {{value}}",
            "groupDetails": [
                { "field": "osName", "label": "Sistema Operacional" }
            ],
            "groupSummaries": [
                { "label": "Máquinas", "aggregate": "count" }
            ],
            "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "clientName", "header": "Cliente" },
                { "field": "siteName", "header": "Site" },
                { "field": "osVersion", "header": "Versão" },
                { "field": "osArchitecture", "header": "Arquitetura" },
                { "field": "totalMemoryBytes", "header": "RAM", "format": "bytes" }
            ],
            "charts": [
                { "type": "doughnut", "title": "Distribuição de SO", "groupField": "osName", "aggregate": "count" },
                { "type": "bar", "title": "Máquinas por SO", "groupField": "osName", "aggregate": "count" }
            ],
            "summaries": [
                { "label": "Total de Máquinas", "aggregate": "count" },
                { "label": "SOs Distintos", "field": "osName", "aggregate": "countDistinct" }
            ],
            "style": {
                "primaryColor": "#0f4c81",
                "headerBackgroundColor": "#1e3a5f"
            }
        }
        """;

    private static string GetHardwareWithSoftwareLayout() => /*lang=json*/ """
        {
            "title": "Hardware + Aplicativos Instalados",
            "subtitle": "Máquinas com +8GB RAM e seus aplicativos",
            "orientation": "landscape",
            "dataSources": [
                { "datasetType": "AgentHardware", "alias": "hw", "filters": { "minTotalMemoryBytes": 8589934592 } },
                { "datasetType": "SoftwareInventory", "alias": "sw", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
            ],
            "groupBy": "hw.agentHostname",
            "groupTitleTemplate": "🖥️ {{value}}",
            "hideGroupColumn": true,
            "groupDetails": [
                { "field": "hw.osName", "label": "SO" },
                { "field": "hw.totalMemoryBytes", "label": "RAM", "format": "bytes" },
                { "field": "hw.processor", "label": "Processador" },
                { "field": "hw.processorCores", "label": "Núcleos" }
            ],
            "groupSummaries": [
                { "label": "Total Apps", "field": "sw.softwareName", "aggregate": "countDistinct" }
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
                { "type": "horizontalBar", "title": "Top 10 Apps", "groupField": "sw.softwareName", "aggregate": "count", "limit": 10 }
            ],
            "summaries": [
                { "label": "Total Máquinas", "aggregate": "count" },
                { "label": "Softwares Distintos", "field": "sw.softwareName", "aggregate": "countDistinct" }
            ]
        }
        """;

    private static string GetVulnerabilityAuditLayout() => /*lang=json*/ """
        {
            "title": "Auditoria de Software - Segurança",
            "subtitle": "Softwares potencialmente desatualizados ou inseguros",
            "orientation": "portrait",
            "groupBy": "agentHostname",
            "groupTitleTemplate": "⚠️ {{value}}",
            "groupDetails": [
                { "field": "agentHostname", "label": "Hostname" },
                { "field": "clientName", "label": "Cliente" },
                { "field": "siteName", "label": "Site" }
            ],
            "columns": [
                { "field": "softwareName", "header": "Software" },
                { "field": "publisher", "header": "Fabricante" },
                { "field": "version", "header": "Versão" },
                { "field": "lastSeenAt", "header": "Última Verificação", "format": "datetime" }
            ],
            "summaries": [
                { "label": "Agentes Afetados", "field": "agentHostname", "aggregate": "countDistinct" },
                { "label": "Softwares Distintos", "field": "softwareName", "aggregate": "countDistinct" }
            ],
            "style": {
                "primaryColor": "#b91c1c",
                "headerBackgroundColor": "#dc2626",
                "headerTextColor": "#ffffff",
                "alternateRowColor": "#fef2f2"
            }
        }
        """;

    private static string GetAgentsWithoutAVLayout() => /*lang=json*/ """
        {
            "title": "Máquinas sem Antivírus Detectado",
            "subtitle": "Agentes onde nenhum AV foi identificado no inventário",
            "orientation": "portrait",
            "groupBy": "clientName",
            "groupTitleTemplate": "🏢 {{value}}",
            "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "siteName", "header": "Site" },
                { "field": "softwareName", "header": "Software Detectado" },
                { "field": "lastSeenAt", "header": "Última Verificação", "format": "datetime" }
            ],
            "summaries": [
                { "label": "Agentes sem AV", "field": "agentHostname", "aggregate": "countDistinct" }
            ],
            "style": {
                "primaryColor": "#b91c1c",
                "headerBackgroundColor": "#dc2626",
                "headerTextColor": "#ffffff"
            }
        }
        """;

    private static string GetConfigAuditLayout() => /*lang=json*/ """
        {
            "title": "Registro de Alterações",
            "subtitle": "Log de auditoria de configuração",
            "orientation": "landscape",
            "groupBy": "entityType",
            "groupTitleTemplate": "📝 {{value}}",
            "columns": [
                { "field": "changedAt", "header": "Data/Hora", "format": "datetime" },
                { "field": "changedBy", "header": "Usuário" },
                { "field": "entityId", "header": "ID Entidade" },
                { "field": "fieldName", "header": "Campo" },
                { "field": "oldValue", "header": "Valor Anterior" },
                { "field": "newValue", "header": "Novo Valor" },
                { "field": "reason", "header": "Motivo" }
            ],
            "charts": [
                { "type": "bar", "title": "Alterações por Dia", "groupField": "changedAt", "aggregate": "count", "bucketBy": "day" }
            ],
            "summaries": [
                { "label": "Total Alterações", "aggregate": "count" },
                { "label": "Usuários Distintos", "field": "changedBy", "aggregate": "countDistinct" }
            ]
        }
        """;

    private static string GetTicketSLALayout() => /*lang=json*/ """
        {
            "title": "Relatório de SLA - Tickets",
            "subtitle": "Últimos 30 dias",
            "orientation": "landscape",
            "groupBy": "priority",
            "groupTitleTemplate": "Prioridade: {{value}}",
            "groupSummaries": [
                { "label": "Total", "aggregate": "count" },
                { "label": "SLA Violados", "field": "slaBreached", "aggregate": "sum" }
            ],
            "columns": [
                { "field": "id", "header": "Ticket #" },
                { "field": "title", "header": "Título" },
                { "field": "priority", "header": "Prioridade" },
                { "field": "createdAt", "header": "Abertura", "format": "datetime" },
                { "field": "slaExpiresAt", "header": "SLA Expira", "format": "datetime" },
                { "field": "slaBreached", "header": "SLA Violado", "conditionalFormat": {
                    "rules": [
                        { "operator": "eq", "value": true, "backgroundColor": "#fee2e2", "textColor": "#dc2626", "icon": "❌" },
                        { "operator": "eq", "value": false, "backgroundColor": "#dcfce7", "textColor": "#16a34a", "icon": "✅" }
                    ]
                }},
                { "field": "closedAt", "header": "Fechamento", "format": "datetime" }
            ],
            "charts": [
                { "type": "doughnut", "title": "Tickets por Prioridade", "groupField": "priority", "aggregate": "count" }
            ],
            "summaries": [
                { "label": "Total Tickets", "aggregate": "count" },
                { "label": "SLA Violados", "field": "slaBreached", "aggregate": "sum" }
            ]
        }
        """;

    private static string GetTicketsByPriorityLayout() => /*lang=json*/ """
        {
            "title": "Tickets por Prioridade",
            "subtitle": "Resumo de tickets ativos",
            "orientation": "portrait",
            "groupBy": "priority",
            "groupTitleTemplate": "🔴 {{value}}",
            "groupSummaries": [
                { "label": "Total", "aggregate": "count" },
                { "label": "SLA Violados", "field": "slaBreached", "aggregate": "sum" }
            ],
            "columns": [
                { "field": "id", "header": "Ticket #" },
                { "field": "title", "header": "Título" },
                { "field": "createdAt", "header": "Abertura", "format": "datetime" },
                { "field": "slaExpiresAt", "header": "SLA Expira", "format": "datetime" },
                { "field": "slaBreached", "header": "SLA", "conditionalFormat": {
                    "rules": [
                        { "operator": "eq", "value": true, "backgroundColor": "#fee2e2", "textColor": "#dc2626", "label": "VIOLADO" },
                        { "operator": "eq", "value": false, "backgroundColor": "#dcfce7", "textColor": "#16a34a", "label": "OK" }
                    ]
                }}
            ],
            "charts": [
                { "type": "doughnut", "title": "Distribuição por Prioridade", "groupField": "priority", "aggregate": "count" }
            ],
            "summaries": [
                { "label": "Total Tickets", "aggregate": "count" }
            ]
        }
        """;

    private static string GetOfflineAgentsLayout() => /*lang=json*/ """
        {
            "title": "Agentes Sem Comunicação",
            "subtitle": "Offline há mais de 24h",
            "orientation": "portrait",
            "groupBy": "clientName",
            "groupTitleTemplate": "🏢 {{value}}",
            "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "siteName", "header": "Site" },
                { "field": "lastSeenAt", "header": "Último Contato", "format": "datetime" },
                { "field": "osName", "header": "SO" },
                { "field": "agentVersion", "header": "Versão Agent" }
            ],
            "summaries": [
                { "label": "Total Offline", "aggregate": "count" },
                { "label": "Clientes Afetados", "field": "clientName", "aggregate": "countDistinct" }
            ],
            "style": {
                "primaryColor": "#b91c1c",
                "headerBackgroundColor": "#dc2626",
                "headerTextColor": "#ffffff",
                "alternateRowColor": "#fef2f2"
            }
        }
        """;

    private static string GetAgentsByLabelLayout() => /*lang=json*/ """
        {
            "title": "Agentes por Label/Tag",
            "subtitle": "Agrupado por labels automáticas",
            "orientation": "landscape",
            "groupBy": "automaticLabels",
            "groupTitleTemplate": "🏷️ {{value}}",
            "groupSummaries": [
                { "label": "Agentes", "aggregate": "count" }
            ],
            "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "clientName", "header": "Cliente" },
                { "field": "siteName", "header": "Site" },
                { "field": "osName", "header": "SO" },
                { "field": "lastSeenAt", "header": "Último Contato", "format": "datetime" }
            ],
            "summaries": [
                { "label": "Labels Distintas", "field": "automaticLabels", "aggregate": "countDistinct" },
                { "label": "Total Agentes", "aggregate": "count" }
            ]
        }
        """;

    private static string GetSystemLogsLayout() => /*lang=json*/ """
        {
            "title": "Logs do Sistema",
            "subtitle": "Últimas 24 horas",
            "orientation": "landscape",
            "groupBy": "level",
            "groupTitleTemplate": "{{value}}",
            "columns": [
                { "field": "createdAt", "header": "Data/Hora", "format": "datetime" },
                { "field": "level", "header": "Nível", "conditionalFormat": {
                    "rules": [
                        { "operator": "eq", "value": "Error", "backgroundColor": "#fee2e2", "textColor": "#dc2626" },
                        { "operator": "eq", "value": "Warning", "backgroundColor": "#fef3c7", "textColor": "#d97706" }
                    ]
                }},
                { "field": "source", "header": "Origem" },
                { "field": "type", "header": "Tipo" },
                { "field": "message", "header": "Mensagem" }
            ],
            "charts": [
                { "type": "doughnut", "title": "Distribuição por Nível", "groupField": "level", "aggregate": "count" },
                { "type": "doughnut", "title": "Distribuição por Origem", "groupField": "source", "aggregate": "count" }
            ],
            "summaries": [
                { "label": "Total Logs", "aggregate": "count" },
                { "label": "Erros", "field": "level", "aggregate": "countDistinct" }
            ]
        }
        """;

    private static string GetAgentCountByClientLayout() => /*lang=json*/ """
        {
            "title": "Agentes por Cliente",
            "subtitle": "Resumo executivo de billing/inventário",
            "orientation": "portrait",
            "coverPage": { "enabled": true, "title": "Resumo por Cliente", "showGeneratedAt": true, "showRowCount": true },
            "groupBy": "clientName",
            "groupTitleTemplate": "🏢 {{value}}",
            "groupDetails": [
                { "field": "clientName", "label": "Cliente" }
            ],
            "groupSummaries": [
                { "label": "Agentes", "aggregate": "count" },
                { "label": "RAM Total (GB)", "field": "totalMemoryBytes", "aggregate": "sum", "format": "bytes" },
                { "label": "Núcleos", "field": "processorCores", "aggregate": "sum" }
            ],
            "columns": [
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "siteName", "header": "Site" },
                { "field": "osName", "header": "SO" },
                { "field": "totalMemoryBytes", "header": "RAM", "format": "bytes" }
            ],
            "charts": [
                { "type": "bar", "title": "Agentes por Cliente", "groupField": "clientName", "aggregate": "count" }
            ],
            "summaries": [
                { "label": "Total Clientes", "field": "clientName", "aggregate": "countDistinct" },
                { "label": "Total Agentes", "aggregate": "count" }
            ],
            "style": {
                "primaryColor": "#0f4c81",
                "headerBackgroundColor": "#1e3a5f"
            }
        }
        """;

    private static string GetSoftwareLicenseCountLayout() => /*lang=json*/ """
        {
            "title": "Contagem de Licenças de Software",
            "subtitle": "Total de instalações por software",
            "orientation": "portrait",
            "groupBy": "softwareName",
            "groupTitleTemplate": "📦 {{value}}",
            "groupSummaries": [
                { "label": "Instalações", "aggregate": "count" }
            ],
            "columns": [
                { "field": "publisher", "header": "Fabricante" },
                { "field": "version", "header": "Versão" },
                { "field": "agentHostname", "header": "Hostname" },
                { "field": "lastSeenAt", "header": "Último Uso", "format": "datetime" }
            ],
            "charts": [
                { "type": "horizontalBar", "title": "Top 15 Softwares", "groupField": "softwareName", "aggregate": "count", "limit": 15 }
            ],
            "summaries": [
                { "label": "Softwares Distintos", "field": "softwareName", "aggregate": "countDistinct" },
                { "label": "Total Instalações", "aggregate": "count" }
            ]
        }
        """;

    private static string GetExecutiveSummaryLayout() => /*lang=json*/ """
        {
            "title": "Resumo Executivo do Parque",
            "subtitle": "Visão consolidada de todos os ativos",
            "orientation": "portrait",
            "coverPage": { "enabled": true, "title": "Resumo Executivo", "subtitle": "Parque de Máquinas", "showGeneratedAt": true, "showRowCount": true },
            "tableOfContents": { "enabled": true },
            "pageHeader": { "left": "Discovery RMM", "center": "{{reportTitle}}", "right": "{{currentDate}}" },
            "pageFooter": { "left": "Confidencial", "center": "Página {{pageNumber}}", "right": "{{clientName}}" },
            "sections": [
                {
                    "title": "📊 Distribuição por SO",
                    "columns": [
                        { "field": "osName", "header": "Sistema Operacional" },
                        { "field": "osVersion", "header": "Versão" },
                        { "field": "osArchitecture", "header": "Arquitetura" }
                    ]
                },
                {
                    "title": "🖥️ Hardware por Máquina",
                    "columns": [
                        { "field": "agentHostname", "header": "Hostname" },
                        { "field": "clientName", "header": "Cliente" },
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
                { "type": "doughnut", "title": "Distribuição de SO", "groupField": "osName", "aggregate": "count" },
                { "type": "horizontalBar", "title": "Máquinas por Cliente", "groupField": "clientName", "aggregate": "count" },
                { "type": "bar", "title": "RAM Total por Cliente (GB)", "groupField": "clientName", "valueField": "totalMemoryBytes", "aggregate": "sum" }
            ],
            "summaries": [
                { "label": "Total de Máquinas", "aggregate": "count" },
                { "label": "Clientes", "field": "clientName", "aggregate": "countDistinct" },
                { "label": "Sites", "field": "siteName", "aggregate": "countDistinct" },
                { "label": "RAM Total (GB)", "field": "totalMemoryBytes", "aggregate": "sum", "format": "bytes" },
                { "label": "Núcleos Totais", "field": "processorCores", "aggregate": "sum" }
            ],
            "style": {
                "primaryColor": "#0f4c81",
                "headerBackgroundColor": "#1e3a5f",
                "fontFamily": "Segoe UI, Arial, sans-serif"
            }
        }
        """;
}
