using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260307042)]
public class M042_PopulateExecutionSchemaJsonForTemplates : Migration
{
    public override void Up()
    {
        // Populate execution_schema_json for each dataset_type with rich customization schemas
        // These schemas include filters, allowed orientations, sort fields, and sample presets

        Execute.Sql(@"
UPDATE report_templates
SET execution_schema_json = CASE dataset_type
    -- SoftwareInventory (0)
    WHEN 0 THEN '{
        ""scope"": ""CurrentSnapshot"",
        ""dateMode"": ""None"",
        ""filters"": [
            {
                ""name"": ""clientId"",
                ""label"": ""Cliente"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por cliente específico""
            },
            {
                ""name"": ""siteId"",
                ""label"": ""Site"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por site específico""
            },
            {
                ""name"": ""agentId"",
                ""label"": ""Agente"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por agente específico""
            },
            {
                ""name"": ""softwareName"",
                ""label"": ""Nome do Software"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Busca parcial por nome""
            },
            {
                ""name"": ""publisher"",
                ""label"": ""Fabricante"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Filtro por fabricante""
            },
            {
                ""name"": ""version"",
                ""label"": ""Versão"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Filtra por versão exata ou parcial""
            }
        ],
        ""allowedOrientations"": [""Portrait"", ""Landscape""],
        ""allowedSortFields"": [""softwareName"", ""publisher"", ""version"", ""lastSeenAt"", ""agentHostname"", ""siteName""],
        ""allowedSortDirections"": [""ASC"", ""DESC""],
        ""sampleFilterPresets"": [
            {
                ""name"": ""Inventário Completo"",
                ""orderBy"": ""softwareName"",
                ""orderDirection"": ""ASC"",
                ""orientation"": ""Landscape""
            },
            {
                ""name"": ""Por Cliente e Site"",
                ""clientId"": 1,
                ""siteId"": 1,
                ""orderBy"": ""lastSeenAt"",
                ""orderDirection"": ""DESC""
            },
            {
                ""name"": ""Por Fabricante (Microsoft)"",
                ""publisher"": ""Microsoft"",
                ""orderBy"": ""softwareName"",
                ""orderDirection"": ""ASC""
            }
        ]
    }'::jsonb

    -- Logs (1)
    WHEN 1 THEN '{
        ""scope"": ""HistoricalTimeSeries"",
        ""dateMode"": ""Range"",
        ""filters"": [
            {
                ""name"": ""from"",
                ""label"": ""Data Inicial"",
                ""type"": ""DateTime"",
                ""required"": true,
                ""description"": ""Início do período (obrigatório)""
            },
            {
                ""name"": ""to"",
                ""label"": ""Data Final"",
                ""type"": ""DateTime"",
                ""required"": true,
                ""description"": ""Fim do período (obrigatório)""
            },
            {
                ""name"": ""clientId"",
                ""label"": ""Cliente"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por cliente específico""
            },
            {
                ""name"": ""siteId"",
                ""label"": ""Site"",
                ""type"": ""Long"",
                ""required"": false
            },
            {
                ""name"": ""agentId"",
                ""label"": ""Agente"",
                ""type"": ""Long"",
                ""required"": false
            },
            {
                ""name"": ""level"",
                ""label"": ""Nível"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Trace, Info, Warning, Error""
            },
            {
                ""name"": ""source"",
                ""label"": ""Origem"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Origem do evento""
            },
            {
                ""name"": ""message"",
                ""label"": ""Mensagem"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Busca parcial no texto""
            }
        ],
        ""allowedOrientations"": [""Portrait"", ""Landscape""],
        ""allowedSortFields"": [""createdAt"", ""level"", ""source"", ""type""],
        ""allowedSortDirections"": [""ASC"", ""DESC""],
        ""sampleFilterPresets"": [
            {
                ""name"": ""Últimas 24 horas"",
                ""from"": ""2026-03-06T00:00:00Z"",
                ""to"": ""2026-03-07T23:59:59Z"",
                ""orderBy"": ""createdAt"",
                ""orderDirection"": ""DESC""
            },
            {
                ""name"": ""Últimos 7 dias - Erros"",
                ""from"": ""2026-02-28T00:00:00Z"",
                ""to"": ""2026-03-07T23:59:59Z"",
                ""level"": ""Error"",
                ""orderBy"": ""createdAt"",
                ""orderDirection"": ""DESC""
            },
            {
                ""name"": ""Este mês por Cliente"",
                ""from"": ""2026-03-01T00:00:00Z"",
                ""to"": ""2026-03-31T23:59:59Z"",
                ""clientId"": 1,
                ""orderBy"": ""createdAt"",
                ""orderDirection"": ""DESC""
            }
        ]
    }'::jsonb

    -- ConfigurationAudit (2)
    WHEN 2 THEN '{
        ""scope"": ""GlobalAudit"",
        ""dateMode"": ""Range"",
        ""filters"": [
            {
                ""name"": ""from"",
                ""label"": ""Data Inicial"",
                ""type"": ""DateTime"",
                ""required"": true,
                ""description"": ""Início do período de auditoria (obrigatório)""
            },
            {
                ""name"": ""to"",
                ""label"": ""Data Final"",
                ""type"": ""DateTime"",
                ""required"": true,
                ""description"": ""Fim do período de auditoria (obrigatório)""
            },
            {
                ""name"": ""entityType"",
                ""label"": ""Tipo de Entidade"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Ex.: Client, Site, ServerConfiguration, Agent""
            },
            {
                ""name"": ""entityId"",
                ""label"": ""ID da Entidade"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por entidade específica""
            },
            {
                ""name"": ""fieldName"",
                ""label"": ""Campo Alterado"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Nome do campo que foi modificado""
            },
            {
                ""name"": ""changedBy"",
                ""label"": ""Alterado Por"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Usuário responsável""
            }
        ],
        ""allowedOrientations"": [""Portrait"", ""Landscape""],
        ""allowedSortFields"": [""changedAt"", ""entityType"", ""changedBy"", ""fieldName""],
        ""allowedSortDirections"": [""ASC"", ""DESC""],
        ""sampleFilterPresets"": [
            {
                ""name"": ""Auditoria do Mês Atual"",
                ""from"": ""2026-03-01T00:00:00Z"",
                ""to"": ""2026-03-31T23:59:59Z"",
                ""orderBy"": ""changedAt"",
                ""orderDirection"": ""DESC""
            },
            {
                ""name"": ""Alterações em Clientes"",
                ""from"": ""2026-03-01T00:00:00Z"",
                ""to"": ""2026-03-31T23:59:59Z"",
                ""entityType"": ""Client"",
                ""orderBy"": ""changedAt"",
                ""orderDirection"": ""DESC""
            },
            {
                ""name"": ""Últimos 7 dias - Todas Alterações"",
                ""from"": ""2026-02-28T00:00:00Z"",
                ""to"": ""2026-03-07T23:59:59Z"",
                ""orderBy"": ""changedAt"",
                ""orderDirection"": ""DESC""
            }
        ]
    }'::jsonb

    -- Tickets (3)
    WHEN 3 THEN '{
        ""scope"": ""CurrentSnapshot"",
        ""dateMode"": ""Range"",
        ""filters"": [
            {
                ""name"": ""from"",
                ""label"": ""Data Inicial (Abertos Após)"",
                ""type"": ""DateTime"",
                ""required"": false,
                ""description"": ""Mostrar tickets criados após esta data""
            },
            {
                ""name"": ""to"",
                ""label"": ""Data Final (Abertos Antes)"",
                ""type"": ""DateTime"",
                ""required"": false,
                ""description"": ""Mostrar tickets criados antes desta data""
            },
            {
                ""name"": ""clientId"",
                ""label"": ""Cliente"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por cliente específico""
            },
            {
                ""name"": ""siteId"",
                ""label"": ""Site"",
                ""type"": ""Long"",
                ""required"": false
            },
            {
                ""name"": ""priority"",
                ""label"": ""Prioridade"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Low, Medium, High, Critical""
            },
            {
                ""name"": ""workflowStateId"",
                ""label"": ""Status"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Estado atual do ticket""
            },
            {
                ""name"": ""slaBreached"",
                ""label"": ""SLA Violado"",
                ""type"": ""Boolean"",
                ""required"": false,
                ""description"": ""true para tickets com SLA violado""
            }
        ],
        ""allowedOrientations"": [""Portrait"", ""Landscape""],
        ""allowedSortFields"": [""createdAt"", ""priority"", ""slaBreached"", ""closedAt""],
        ""allowedSortDirections"": [""ASC"", ""DESC""],
        ""sampleFilterPresets"": [
            {
                ""name"": ""Últimos 7 dias"",
                ""from"": ""2026-02-28T00:00:00Z"",
                ""to"": ""2026-03-07T23:59:59Z"",
                ""orderBy"": ""createdAt"",
                ""orderDirection"": ""DESC""
            },
            {
                ""name"": ""Este mês"",
                ""from"": ""2026-03-01T00:00:00Z"",
                ""to"": ""2026-03-31T23:59:59Z"",
                ""orderBy"": ""createdAt"",
                ""orderDirection"": ""DESC""
            },
            {
                ""name"": ""Alta prioridade - Últimos 30 dias"",
                ""from"": ""2026-02-05T00:00:00Z"",
                ""to"": ""2026-03-07T23:59:59Z"",
                ""priority"": ""High"",
                ""orderBy"": ""priority"",
                ""orderDirection"": ""ASC""
            },
            {
                ""name"": ""SLA Violado"",
                ""slaBreached"": true,
                ""orderBy"": ""createdAt"",
                ""orderDirection"": ""DESC""
            }
        ]
    }'::jsonb

    -- AgentHardware (4)
    WHEN 4 THEN '{
        ""scope"": ""CurrentSnapshot"",
        ""dateMode"": ""None"",
        ""filters"": [
            {
                ""name"": ""clientId"",
                ""label"": ""Cliente"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por cliente específico""
            },
            {
                ""name"": ""siteId"",
                ""label"": ""Site"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por site específico""
            },
            {
                ""name"": ""agentId"",
                ""label"": ""Agente"",
                ""type"": ""Long"",
                ""required"": false,
                ""description"": ""Filtrar por agente específico""
            },
            {
                ""name"": ""osName"",
                ""label"": ""Sistema Operacional"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Ex.: Windows 10, Windows Server 2019, Ubuntu""
            },
            {
                ""name"": ""processor"",
                ""label"": ""Processador"",
                ""type"": ""String"",
                ""required"": false,
                ""description"": ""Filtro por tipo de processador""
            }
        ],
        ""allowedOrientations"": [""Portrait"", ""Landscape""],
        ""allowedSortFields"": [""siteName"", ""agentHostname"", ""collectedAt"", ""osName"", ""processor""],
        ""allowedSortDirections"": [""ASC"", ""DESC""],
        ""sampleFilterPresets"": [
            {
                ""name"": ""Inventário Geral"",
                ""orderBy"": ""siteName"",
                ""orderDirection"": ""ASC"",
                ""orientation"": ""Landscape""
            },
            {
                ""name"": ""Por Cliente"",
                ""clientId"": 1,
                ""orderBy"": ""siteName"",
                ""orderDirection"": ""ASC""
            },
            {
                ""name"": ""Windows - Ordenado por Host"",
                ""osName"": ""Windows"",
                ""orderBy"": ""agentHostname"",
                ""orderDirection"": ""ASC""
            },
            {
                ""name"": ""Coleta Recente"",
                ""orderBy"": ""collectedAt"",
                ""orderDirection"": ""DESC""
            }
        ]
    }'::jsonb

    ELSE execution_schema_json
END
WHERE execution_schema_json IS NULL;
");
    }

    public override void Down()
    {
        // Revert by clearing the populated schemas
        Execute.Sql(@"
UPDATE report_templates
SET execution_schema_json = NULL
WHERE execution_schema_json IS NOT NULL;
");
    }
}
