using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260307_042, "Populate execution_schema_json for existing report templates")]
public class M042_PopulateExecutionSchemaJsonForTemplates : Migration
{
    public override void Up()
    {
        UpdateSchemaIfNull(0, SoftwareInventorySchema);
        UpdateSchemaIfNull(1, LogsSchema);
        UpdateSchemaIfNull(2, ConfigurationAuditSchema);
        UpdateSchemaIfNull(3, TicketsSchema);
        UpdateSchemaIfNull(4, AgentHardwareSchema);
    }

    public override void Down()
    {
        Update.Table("report_templates")
            .Set(new { execution_schema_json = (string?)null })
            .AllRows();
    }

    private void UpdateSchemaIfNull(int datasetType, string schemaJson)
    {
        Update.Table("report_templates")
            .Set(new { execution_schema_json = schemaJson })
            .Where(new
            {
                dataset_type = datasetType,
                execution_schema_json = (string?)null
            });
    }

    private const string SoftwareInventorySchema = """
{
  "scopeType": 3,
  "dateMode": 0,
  "allowedOrientations": ["landscape", "portrait"],
  "defaultOrientation": "landscape",
  "allowedSortFields": ["softwareName", "publisher", "version", "lastSeenAt", "agentHostname", "siteName"],
  "defaultSortField": "softwareName",
  "allowedSortDirections": ["asc", "desc"],
  "defaultSortDirection": "asc",
  "filters": [
    { "name": "clientId", "label": "Cliente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por cliente.", "uiComponent": 4, "placeholder": "GUID do cliente" },
    { "name": "siteId", "label": "Site", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por site.", "uiComponent": 4, "dependsOn": "clientId", "placeholder": "GUID do site" },
    { "name": "agentId", "label": "Agente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por agente.", "uiComponent": 4, "dependsOn": "siteId", "placeholder": "GUID do agente" },
    { "name": "softwareName", "label": "Software", "type": 0, "required": false, "group": "Filtros", "description": "Busca parcial por nome do software.", "uiComponent": 1, "maxLength": 200, "isPartialMatch": true },
    { "name": "publisher", "label": "Fabricante", "type": 0, "required": false, "group": "Filtros", "description": "Busca parcial por fabricante.", "uiComponent": 1, "maxLength": 200, "isPartialMatch": true },
    { "name": "version", "label": "Versao", "type": 0, "required": false, "group": "Filtros", "description": "Filtra por versao exata ou parcial.", "uiComponent": 0, "maxLength": 100, "isPartialMatch": true },
    { "name": "orderBy", "label": "Ordenar por", "type": 2, "required": false, "group": "Ordenacao", "description": "Campo para ordenacao dos resultados.", "uiComponent": 2, "allowedValues": ["softwareName", "publisher", "version", "lastSeenAt", "agentHostname", "siteName"], "defaultValue": "softwareName" },
    { "name": "orderDirection", "label": "Direcao", "type": 2, "required": false, "group": "Ordenacao", "description": "Direcao da ordenacao.", "uiComponent": 2, "allowedValues": ["asc", "desc"], "defaultValue": "asc" },
    { "name": "orientation", "label": "Orientacao", "type": 2, "required": false, "group": "Formatacao", "description": "Orientacao da pagina do relatorio.", "uiComponent": 2, "allowedValues": ["landscape", "portrait"], "defaultValue": "landscape" },
    { "name": "limit", "label": "Limite de linhas", "type": 4, "required": false, "group": "Saida", "description": "Maximo recomendado: 10000.", "uiComponent": 5, "defaultValue": "1000", "min": 1, "max": 10000 }
  ],
  "sampleFilterPresets": [
    { "name": "Inventario completo", "description": "Todos os clientes, ordenado por software", "filtersJson": "{\"limit\":5000,\"orderBy\":\"softwareName\",\"orderDirection\":\"asc\",\"orientation\":\"landscape\"}" },
    { "name": "Por cliente/site", "description": "Escopo especifico com ordenacao por ultima visualizacao", "filtersJson": "{\"clientId\":\"<guid>\",\"siteId\":\"<guid>\",\"limit\":3000,\"orderBy\":\"lastSeenAt\",\"orderDirection\":\"desc\"}" }
  ]
}
""";

    private const string LogsSchema = """
{
  "scopeType": 3,
  "dateMode": 2,
  "allowedOrientations": ["landscape", "portrait"],
  "defaultOrientation": "portrait",
  "allowedSortFields": ["timestamp", "level", "source", "type"],
  "defaultSortField": "timestamp",
  "allowedSortDirections": ["asc", "desc"],
  "defaultSortDirection": "desc",
  "filters": [
    { "name": "from", "label": "Data inicial", "type": 7, "required": true, "group": "Periodo", "description": "Inicio do intervalo de logs (obrigatorio).", "uiComponent": 7 },
    { "name": "to", "label": "Data final", "type": 7, "required": true, "group": "Periodo", "description": "Fim do intervalo de logs (obrigatorio).", "uiComponent": 7 },
    { "name": "clientId", "label": "Cliente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por cliente.", "uiComponent": 4, "placeholder": "GUID do cliente" },
    { "name": "siteId", "label": "Site", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por site.", "uiComponent": 4, "dependsOn": "clientId", "placeholder": "GUID do site" },
    { "name": "agentId", "label": "Agente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por agente.", "uiComponent": 4, "dependsOn": "siteId", "placeholder": "GUID do agente" },
    { "name": "level", "label": "Nivel", "type": 2, "required": false, "group": "Filtros", "description": "Nivel do log.", "uiComponent": 2, "allowedValues": ["Trace", "Debug", "Info", "Warning", "Error", "Critical"] },
    { "name": "source", "label": "Origem", "type": 0, "required": false, "group": "Filtros", "description": "Origem do evento.", "uiComponent": 0, "maxLength": 100 },
    { "name": "message", "label": "Mensagem", "type": 0, "required": false, "group": "Filtros", "description": "Busca parcial no texto da mensagem.", "uiComponent": 1, "maxLength": 1000, "isPartialMatch": true },
    { "name": "orderBy", "label": "Ordenar por", "type": 2, "required": false, "group": "Ordenacao", "description": "Campo para ordenacao dos resultados.", "uiComponent": 2, "allowedValues": ["timestamp", "level", "source", "type"], "defaultValue": "timestamp" },
    { "name": "orderDirection", "label": "Direcao", "type": 2, "required": false, "group": "Ordenacao", "description": "Direcao da ordenacao.", "uiComponent": 2, "allowedValues": ["asc", "desc"], "defaultValue": "desc" },
    { "name": "orientation", "label": "Orientacao", "type": 2, "required": false, "group": "Formatacao", "description": "Orientacao da pagina do relatorio.", "uiComponent": 2, "allowedValues": ["landscape", "portrait"], "defaultValue": "portrait" },
    { "name": "limit", "label": "Limite de linhas", "type": 4, "required": false, "group": "Saida", "description": "Maximo recomendado: 10000.", "uiComponent": 5, "defaultValue": "1000", "min": 1, "max": 10000 }
  ]
}
""";

    private const string ConfigurationAuditSchema = """
{
  "scopeType": 0,
  "dateMode": 2,
  "allowedOrientations": ["landscape", "portrait"],
  "defaultOrientation": "portrait",
  "allowedSortFields": ["timestamp", "entityType", "changedBy", "fieldName"],
  "defaultSortField": "timestamp",
  "allowedSortDirections": ["asc", "desc"],
  "defaultSortDirection": "desc",
  "filters": [
    { "name": "from", "label": "Data inicial", "type": 7, "required": true, "group": "Periodo", "description": "Inicio do intervalo de auditoria (obrigatorio).", "uiComponent": 7 },
    { "name": "to", "label": "Data final", "type": 7, "required": true, "group": "Periodo", "description": "Fim do intervalo de auditoria (obrigatorio).", "uiComponent": 7 },
    { "name": "entityType", "label": "Tipo de entidade", "type": 0, "required": false, "group": "Filtros", "description": "Ex.: Client, Site, ServerConfiguration.", "uiComponent": 0, "maxLength": 100 },
    { "name": "entityId", "label": "ID da entidade", "type": 3, "required": false, "group": "Filtros", "description": "Filtrar por entidade especifica.", "uiComponent": 4 },
    { "name": "fieldName", "label": "Campo alterado", "type": 0, "required": false, "group": "Filtros", "description": "Nome do campo alterado.", "uiComponent": 0, "maxLength": 100 },
    { "name": "changedBy", "label": "Alterado por", "type": 0, "required": false, "group": "Filtros", "description": "Usuario responsavel pela alteracao.", "uiComponent": 0, "maxLength": 256 },
    { "name": "reason", "label": "Motivo", "type": 0, "required": false, "group": "Filtros", "description": "Texto livre de motivo da alteracao.", "uiComponent": 1, "maxLength": 1000, "isPartialMatch": true },
    { "name": "orderBy", "label": "Ordenar por", "type": 2, "required": false, "group": "Ordenacao", "description": "Campo para ordenacao dos resultados.", "uiComponent": 2, "allowedValues": ["timestamp", "entityType", "changedBy", "fieldName"], "defaultValue": "timestamp" },
    { "name": "orderDirection", "label": "Direcao", "type": 2, "required": false, "group": "Ordenacao", "description": "Direcao da ordenacao.", "uiComponent": 2, "allowedValues": ["asc", "desc"], "defaultValue": "desc" },
    { "name": "orientation", "label": "Orientacao", "type": 2, "required": false, "group": "Formatacao", "description": "Orientacao da pagina do relatorio.", "uiComponent": 2, "allowedValues": ["landscape", "portrait"], "defaultValue": "portrait" },
    { "name": "limit", "label": "Limite de linhas", "type": 4, "required": false, "group": "Saida", "description": "Maximo recomendado: 10000.", "uiComponent": 5, "defaultValue": "1000", "min": 1, "max": 10000 }
  ]
}
""";

    private const string TicketsSchema = """
{
  "scopeType": 3,
  "dateMode": 1,
  "allowedOrientations": ["landscape", "portrait"],
  "defaultOrientation": "landscape",
  "allowedSortFields": ["timestamp", "priority", "slaBreached", "closedAt"],
  "defaultSortField": "timestamp",
  "allowedSortDirections": ["asc", "desc"],
  "defaultSortDirection": "desc",
  "filters": [
    { "name": "clientId", "label": "Cliente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por cliente.", "uiComponent": 4 },
    { "name": "siteId", "label": "Site", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por site.", "uiComponent": 4, "dependsOn": "clientId" },
    { "name": "agentId", "label": "Agente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por agente.", "uiComponent": 4, "dependsOn": "siteId" },
    { "name": "workflowStateId", "label": "Status workflow", "type": 3, "required": false, "group": "Filtros", "description": "Estado atual do ticket.", "uiComponent": 4 },
    { "name": "priority", "label": "Prioridade", "type": 2, "required": false, "group": "Filtros", "description": "Prioridade do ticket.", "uiComponent": 2, "allowedValues": ["Low", "Medium", "High", "Critical"] },
    { "name": "slaBreached", "label": "SLA violado", "type": 8, "required": false, "group": "Filtros", "description": "true/false.", "uiComponent": 8 },
    { "name": "from", "label": "Data inicial", "type": 7, "required": false, "group": "Periodo", "description": "Inicio opcional do periodo.", "uiComponent": 7 },
    { "name": "to", "label": "Data final", "type": 7, "required": false, "group": "Periodo", "description": "Fim opcional do periodo.", "uiComponent": 7 },
    { "name": "orderBy", "label": "Ordenar por", "type": 2, "required": false, "group": "Ordenacao", "description": "Campo para ordenacao dos resultados.", "uiComponent": 2, "allowedValues": ["timestamp", "priority", "slaBreached", "closedAt"], "defaultValue": "timestamp" },
    { "name": "orderDirection", "label": "Direcao", "type": 2, "required": false, "group": "Ordenacao", "description": "Direcao da ordenacao.", "uiComponent": 2, "allowedValues": ["asc", "desc"], "defaultValue": "desc" },
    { "name": "orientation", "label": "Orientacao", "type": 2, "required": false, "group": "Formatacao", "description": "Orientacao da pagina do relatorio.", "uiComponent": 2, "allowedValues": ["landscape", "portrait"], "defaultValue": "landscape" },
    { "name": "limit", "label": "Limite de linhas", "type": 4, "required": false, "group": "Saida", "description": "Maximo recomendado: 10000.", "uiComponent": 5, "defaultValue": "1000", "min": 1, "max": 10000 }
  ]
}
""";

    private const string AgentHardwareSchema = """
{
  "scopeType": 3,
  "dateMode": 0,
  "allowedOrientations": ["landscape", "portrait"],
  "defaultOrientation": "landscape",
  "allowedSortFields": ["siteName", "agentHostname", "collectedAt", "osName"],
  "defaultSortField": "siteName",
  "allowedSortDirections": ["asc", "desc"],
  "defaultSortDirection": "asc",
  "filters": [
    { "name": "clientId", "label": "Cliente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por cliente.", "uiComponent": 4 },
    { "name": "siteId", "label": "Site", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por site.", "uiComponent": 4, "dependsOn": "clientId" },
    { "name": "agentId", "label": "Agente", "type": 3, "required": false, "group": "Escopo", "description": "Escopo opcional por agente.", "uiComponent": 4, "dependsOn": "siteId" },
    { "name": "osName", "label": "Sistema operacional", "type": 0, "required": false, "group": "Filtros", "description": "Filtra por nome do SO.", "uiComponent": 1, "maxLength": 150, "isPartialMatch": true },
    { "name": "processor", "label": "Processador", "type": 0, "required": false, "group": "Filtros", "description": "Filtra por processador.", "uiComponent": 1, "maxLength": 200, "isPartialMatch": true },
    { "name": "orderBy", "label": "Ordenar por", "type": 2, "required": false, "group": "Ordenacao", "description": "Campo para ordenacao dos resultados.", "uiComponent": 2, "allowedValues": ["siteName", "agentHostname", "collectedAt", "osName"], "defaultValue": "siteName" },
    { "name": "orderDirection", "label": "Direcao", "type": 2, "required": false, "group": "Ordenacao", "description": "Direcao da ordenacao.", "uiComponent": 2, "allowedValues": ["asc", "desc"], "defaultValue": "asc" },
    { "name": "orientation", "label": "Orientacao", "type": 2, "required": false, "group": "Formatacao", "description": "Orientacao da pagina do relatorio.", "uiComponent": 2, "allowedValues": ["landscape", "portrait"], "defaultValue": "landscape" },
    { "name": "limit", "label": "Limite de linhas", "type": 4, "required": false, "group": "Saida", "description": "Maximo recomendado: 10000.", "uiComponent": 5, "defaultValue": "1000", "min": 1, "max": 10000 }
  ]
}
""";
}
