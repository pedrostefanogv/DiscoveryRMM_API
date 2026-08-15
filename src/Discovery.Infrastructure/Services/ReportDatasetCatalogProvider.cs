using Discovery.Core.Cqrs.Reports.Queries;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Fornece o catálogo estático de datasets disponíveis para relatórios.
/// Os metadados (campos, filtros, capacidades de join) espelham as consultas
/// implementadas em <see cref="ReportDatasetQueryService"/>.
/// </summary>
public interface IReportDatasetCatalogProvider
{
    IReadOnlyList<ReportDatasetCatalogItemDto> GetAll();
}

public sealed class ReportDatasetCatalogProvider : IReportDatasetCatalogProvider
{
    private static readonly string[] AllFormats = ["xlsx", "pdf", "csv", "markdown"];

    private static readonly string[] JoinAgent = ["agentId"];
    private static readonly string[] JoinAgentClientSite = ["agentId", "clientId", "siteId"];
    private static readonly string[] JoinClientSite = ["clientId", "siteId"];
    private static readonly string[] JoinClient = ["clientId"];

    private readonly IReadOnlyList<ReportDatasetCatalogItemDto> _catalog;

    public ReportDatasetCatalogProvider()
    {
        _catalog = BuildCatalog();
    }

    public IReadOnlyList<ReportDatasetCatalogItemDto> GetAll() => _catalog;

    private static IReadOnlyList<ReportDatasetCatalogItemDto> BuildCatalog()
    {
        var items = new List<ReportDatasetCatalogItemDto>();

        void Add(
            string key,
            int datasetType,
            string name,
            string description,
            string[] fields,
            string[] joinKeys,
            string[] filters,
            params (string Field, string Label, string DataType, bool IsJoinKey)[] metadata)
        {
            var fieldMetadata = fields
                .Select(field =>
                {
                    var meta = metadata.FirstOrDefault(m => m.Field == field);
                    return new ReportDatasetFieldMetadataDto(
                        Field: field,
                        Label: meta.Label ?? FormatLabel(field),
                        DataType: meta.DataType ?? InferDataType(field),
                        IsJoinKey: joinKeys.Contains(field) || meta.IsJoinKey,
                        // Alias único por campo: {prefixo do dataset}.{campo}
                        // (ex: "sw.softwareName"). Garante unicidade global, já que
                        // o prefixo sozinho se repetiria para todos os campos.
                        DefaultAlias: $"{GetDefaultAlias(key)}.{field}",
                        DatasetName: name);
                })
                .ToList();

            var filterDefs = filters
                .Select(f => new ReportDatasetFilterDto(f, InferFilterType(f), Required: false, Label: FormatLabel(f)))
                .ToList();

            // Join capabilities devem referenciar APENAS campos que existem no
            // dataset (fields). Filtra chaves de join ausentes (ex: um dataset
            // que expõe clientName em vez de clientId).
            var effectiveJoinKeys = joinKeys.Where(k => fields.Contains(k)).ToArray();
            var joinCapabilities = effectiveJoinKeys
                .Select(k => new ReportDatasetJoinCapabilityDto(
                    SourceKey: k,
                    TargetKey: k,
                    JoinTypes: ["left", "inner"],
                    Description: $"Combina pelo campo {k}"))
                .ToList();

            items.Add(new ReportDatasetCatalogItemDto(
                Key: key,
                Type: key,
                DatasetType: datasetType,
                Name: name,
                Description: description,
                Fields: fields,
                FieldMetadata: fieldMetadata,
                Filters: filterDefs,
                JoinCapabilities: joinCapabilities,
                DefaultFormat: "xlsx",
                SupportedFormats: AllFormats));
        }

        Add("softwareInventory", 0, "Inventário de Software",
            "Aplicativos instalados nos agentes, com versão, fabricante e último visto.",
            ["clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "softwareName", "publisher", "version", "lastSeenAt"],
            JoinAgentClientSite,
            ["siteId", "agentId", "softwareName"],
            ("agentId", "Agente", "guid", true),
            ("softwareName", "Software", "string", false),
            ("publisher", "Fabricante", "string", false),
            ("version", "Versão", "string", false),
            ("lastSeenAt", "Última vez visto", "datetime", false));

        Add("logs", 1, "Logs",
            "Registros de log do sistema e dos agentes, com nível, origem e mensagem.",
            ["id", "siteId", "agentId", "type", "level", "source", "message", "createdAt"],
            JoinAgentClientSite,
            ["siteId", "agentId", "from", "to"],
            ("agentId", "Agente", "guid", true),
            ("level", "Nível", "string", false),
            ("source", "Origem", "string", false),
            ("message", "Mensagem", "string", false),
            ("createdAt", "Data", "datetime", false));

        Add("configurationAudit", 2, "Auditoria de Configuração",
            "Histórico de alterações de configuração, com entidade, campo e responsável.",
            ["entityType", "entityId", "fieldName", "oldValue", "newValue", "reason", "changedBy", "changedAt"],
            JoinAgentClientSite,
            ["from", "to", "changedBy"],
            ("entityType", "Entidade", "string", false),
            ("fieldName", "Campo", "string", false),
            ("changedBy", "Alterado por", "string", false),
            ("changedAt", "Alterado em", "datetime", false));

        Add("tickets", 3, "Chamados (Tickets)",
            "Tickets abertos, prioridade, SLA e datas.",
            ["id", "siteId", "agentId", "title", "priority", "workflowStateId", "slaExpiresAt", "slaBreached", "createdAt", "closedAt"],
            JoinAgentClientSite,
            ["siteId", "workflowStateId", "from", "to"],
            ("agentId", "Agente", "guid", true),
            ("title", "Título", "string", false),
            ("priority", "Prioridade", "string", false),
            ("createdAt", "Aberto em", "datetime", false),
            ("closedAt", "Fechado em", "datetime", false));

        Add("agentHardware", 4, "Hardware dos Agentes",
            "Inventário de hardware: SO, processador, memória, GPU, discos, placa-mãe e BIOS.",
            ["clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "osName", "osVersion", "osBuild", "osArchitecture", "processor", "processorCores", "processorThreads", "processorArchitecture", "processorFrequencyGhz", "processorSocket", "processorTdpWatts", "totalMemoryGB", "totalMemoryBytes", "gpuModel", "gpuMemoryGB", "gpuDriverVersion", "totalDisksCount", "motherboardManufacturer", "motherboardModel", "biosVersion", "biosManufacturer", "biosDate", "biosSerialNumber", "inventorySchemaVersion", "inventoryCollectedAt", "collectedAt"],
            JoinAgentClientSite,
            ["siteId", "agentId"],
            ("agentId", "Agente", "guid", true),
            ("agentHostname", "Hostname", "string", false),
            ("osName", "SO", "string", false),
            ("processor", "Processador", "string", false),
            ("totalMemoryGB", "RAM (GB)", "number", false),
            ("totalDisksCount", "Discos", "number", false),
            ("collectedAt", "Coletado em", "datetime", false));

        Add("agentInventoryComposite", 5, "Inventário Composto",
            "Software + hardware por agente em uma única fonte.",
            ["clientName", "siteName", "agentId", "agentHostname", "automaticLabels", "osName", "osVersion", "processor", "totalMemoryGB", "totalDisksCount", "softwareName", "publisher", "softwareVersion", "softwareLastSeenAt", "hardwareCollectedAt"],
            JoinAgentClientSite,
            ["siteId", "agentId", "softwareName"],
            ("agentId", "Agente", "guid", true),
            ("agentHostname", "Hostname", "string", false),
            ("softwareName", "Software", "string", false),
            ("osName", "SO", "string", false),
            ("totalMemoryGB", "RAM (GB)", "number", false));

        Add("agentLabels", 6, "Labels dos Agentes",
            "Labels aplicados aos agentes, com origem e data de aplicação.",
            ["agentId", "agentHostname", "agentName", "clientId", "clientName", "siteId", "siteName", "labelName", "labelSource", "labelAppliedAt"],
            JoinAgentClientSite,
            ["siteId", "agentId", "labelName"],
            ("agentId", "Agente", "guid", true),
            ("labelName", "Label", "string", false),
            ("labelSource", "Origem", "string", false),
            ("labelAppliedAt", "Aplicado em", "datetime", false));

        Add("automaticLabelRules", 7, "Regras de Labels Automáticas",
            "Regras de rotulagem automática e seus impactos.",
            ["ruleId", "ruleName", "labelName", "ruleDescription", "isActive", "conditionExpression", "matchCount", "affectedAgentHostnames", "createdAt"],
            ["labelName", "ruleId"],
            ["labelName"],
            ("ruleId", "Regra", "guid", true),
            ("ruleName", "Nome da regra", "string", false),
            ("labelName", "Label", "string", true),
            ("matchCount", "Agentes afetados", "number", false));

        Add("automationExecutions", 8, "Execuções de Automação",
            "Histórico de execuções de tarefas e scripts de automação.",
            ["executionId", "commandId", "agentId", "agentHostname", "siteId", "siteName", "taskId", "scriptId", "sourceType", "status", "exitCode", "errorMessage", "createdAt", "completedAt"],
            ["agentId"],
            ["siteId", "agentId", "taskId", "scriptId"],
            ("agentId", "Agente", "guid", true),
            ("agentHostname", "Hostname", "string", false),
            ("status", "Status", "string", false),
            ("exitCode", "Exit Code", "number", false),
            ("createdAt", "Criado em", "datetime", false));

        Add("agentMonitoringEvents", 9, "Eventos de Monitoramento",
            "Eventos de monitoramento de agentes com alertas e métricas.",
            ["id", "clientId", "siteId", "agentId", "alertCode", "severity", "title", "message", "metricKey", "metricValue", "source", "correlationId", "occurredAt", "createdAt"],
            JoinAgentClientSite,
            ["siteId", "agentId", "severity", "from", "to"],
            ("agentId", "Agente", "guid", true),
            ("severity", "Severidade", "string", false),
            ("title", "Título", "string", false),
            ("occurredAt", "Ocorrido em", "datetime", false));

        Add("agentAlerts", 10, "Alertas dos Agentes",
            "Definições de alertas, limiares e escopos.",
            ["id", "clientId", "name", "severity", "enabled", "metricKey", "threshold", "scopeType", "createdAt", "updatedAt"],
            JoinClient,
            ["severity"],
            ("name", "Nome", "string", false),
            ("severity", "Severidade", "string", false),
            ("threshold", "Limiar", "number", false));

        Add("p2pTelemetry", 11, "Telemetria P2P",
            "Métricas de replicação e desempenho dos peers P2P.",
            ["id", "agentId", "siteId", "clientId", "collectedAt", "receivedAt", "publishedArtifacts", "replicationsSucceeded", "replicationsFailed", "bytesServed", "bytesDownloaded", "activeReplications", "hostCpuPercent", "hostMemoryPercent", "hostDiskBusyPercent", "hostCpuCores", "hostRamGB", "knownPeers", "connectedPeers", "planTotalAgents", "planSelectedSeeds"],
            JoinAgentClientSite,
            ["siteId", "agentId"],
            ("agentId", "Agente", "guid", true),
            ("bytesServed", "Bytes servidos", "number", false),
            ("bytesDownloaded", "Bytes baixados", "number", false),
            ("knownPeers", "Peers conhecidos", "number", false),
            ("connectedPeers", "Peers conectados", "number", false));

        Add("agentDisks", 12, "Discos dos Agentes",
            "Informações de discos: tamanho, uso, sistema de arquivos e saúde.",
            ["agentId", "agentHostname", "siteName", "clientName", "diskName", "sizeBytes", "freeBytes", "fileSystem", "interface", "type", "serialNumber", "healthStatus", "collectedAt"],
            JoinAgentClientSite,
            ["siteId", "agentId"],
            ("agentId", "Agente", "guid", true),
            ("diskName", "Disco", "string", false),
            ("sizeBytes", "Tamanho (bytes)", "number", false),
            ("freeBytes", "Livre (bytes)", "number", false),
            ("healthStatus", "Saúde", "string", false));

        Add("networkAdapters", 13, "Adaptadores de Rede",
            "Adaptadores de rede dos agentes com endereços e velocidade.",
            ["agentId", "agentHostname", "siteName", "clientName", "adapterName", "macAddress", "ipAddresses", "speedMbps", "isDefault", "collectedAt"],
            JoinAgentClientSite,
            ["agentId"],
            ("agentId", "Agente", "guid", true),
            ("adapterName", "Adaptador", "string", false),
            ("macAddress", "MAC", "string", false),
            ("ipAddresses", "IPs", "string", false),
            ("speedMbps", "Velocidade (Mbps)", "number", false));

        Add("listeningPorts", 14, "Portas em Escuta",
            "Portas TCP/UDP em escuta nos agentes.",
            ["agentId", "agentHostname", "siteName", "clientName", "port", "protocol", "processName", "state", "collectedAt"],
            JoinAgentClientSite,
            ["agentId", "port", "processName", "state"],
            ("agentId", "Agente", "guid", true),
            ("port", "Porta", "number", false),
            ("protocol", "Protocolo", "string", false),
            ("processName", "Processo", "string", false));

        Add("printers", 15, "Impressoras",
            "Impressoras instaladas nos agentes.",
            ["agentId", "agentHostname", "siteName", "clientName", "printerName", "driverName", "portName", "isDefault", "isShared", "collectedAt"],
            JoinAgentClientSite,
            ["agentId"],
            ("agentId", "Agente", "guid", true),
            ("printerName", "Impressora", "string", false),
            ("driverName", "Driver", "string", false),
            ("isDefault", "Padrão", "boolean", false));

        Add("softwareCatalog", 16, "Catálogo de Software",
            "Catálogo central de softwares conhecidos.",
            ["id", "name", "publisher", "category", "latestVersion", "eolDate", "isEol", "licenseType", "updatedAt"],
            JoinClient,
            ["publisher"],
            ("name", "Nome", "string", false),
            ("publisher", "Fabricante", "string", false),
            ("category", "Categoria", "string", false),
            ("latestVersion", "Última versão", "string", false),
            ("isEol", "Fim de vida", "boolean", false));

        Add("automationScripts", 17, "Scripts de Automação",
            "Scripts de automação disponíveis.",
            ["id", "clientId", "name", "language", "isActive", "createdAt", "updatedAt"],
            JoinClient,
            ["language"],
            ("name", "Nome", "string", false),
            ("language", "Linguagem", "string", false),
            ("isActive", "Ativo", "boolean", false));

        Add("appPackages", 18, "Pacotes de Aplicativos",
            "Pacotes de aplicativos gerenciados no sistema.",
            ["id", "name", "publisher", "version", "source", "category", "isActive", "description", "updatedAt"],
            JoinClient,
            ["source", "category"],
            ("name", "Nome", "string", false),
            ("publisher", "Fabricante", "string", false),
            ("version", "Versão", "string", false),
            ("category", "Categoria", "string", false));

        Add("ticketActivity", 19, "Atividade de Chamados",
            "Log de ações em tickets.",
            ["id", "ticketId", "clientId", "action", "changedBy", "oldValue", "newValue", "createdAt"],
            JoinClient,
            ["ticketId", "action", "from", "to"],
            ("ticketId", "Ticket", "guid", true),
            ("action", "Ação", "string", false),
            ("changedBy", "Alterado por", "string", false),
            ("createdAt", "Data", "datetime", false));

        Add("ticketEscalations", 20, "Regras de Escalonamento",
            "Regras de escalonamento de tickets.",
            ["id", "clientId", "name", "escalationLevel", "isActive", "createdAt", "updatedAt"],
            JoinClient,
            ["escalationLevel"],
            ("name", "Nome", "string", false),
            ("escalationLevel", "Nível", "number", false),
            ("isActive", "Ativo", "boolean", false));

        Add("customFields", 21, "Campos Personalizados",
            "Definições de campos personalizados por entidade.",
            ["id", "clientId", "entityName", "fieldName", "valueType", "isRequired", "isActive", "createdAt", "updatedAt"],
            JoinClient,
            ["entityName"],
            ("entityName", "Entidade", "string", false),
            ("fieldName", "Campo", "string", false),
            ("valueType", "Tipo", "string", false),
            ("isRequired", "Obrigatório", "boolean", false));

        Add("knowledgeBase", 22, "Base de Conhecimento",
            "Artigos da base de conhecimento.",
            ["id", "clientId", "title", "category", "status", "author", "updatedAt", "createdAt"],
            JoinClient,
            ["status", "category"],
            ("title", "Título", "string", false),
            ("category", "Categoria", "string", false),
            ("status", "Status", "string", false),
            ("author", "Autor", "string", false),
            ("updatedAt", "Atualizado em", "datetime", false));

        return items.AsReadOnly();
    }

    private static string GetDefaultAlias(string key) => key switch
    {
        "softwareInventory" => "sw",
        "agentHardware" => "hw",
        "agentLabels" => "lbl",
        "automaticLabelRules" => "rule",
        "logs" => "log",
        "tickets" => "tk",
        "configurationAudit" => "audit",
        "automationExecutions" => "auto",
        "agentInventoryComposite" => "inv",
        "agentMonitoringEvents" => "mon",
        "agentAlerts" => "alrt",
        "p2pTelemetry" => "p2p",
        "agentDisks" => "dsk",
        "networkAdapters" => "net",
        "listeningPorts" => "prt",
        "printers" => "prn",
        "softwareCatalog" => "swc",
        "automationScripts" => "scr",
        "appPackages" => "app",
        "ticketActivity" => "tka",
        "ticketEscalations" => "esc",
        "customFields" => "cf",
        "knowledgeBase" => "kb",
        _ => key[..Math.Min(3, key.Length)]
    };

    private static string FormatLabel(string field)
    {
        if (field.Length == 0) return field;
        var spaced = System.Text.RegularExpressions.Regex.Replace(field, "([a-z])([A-Z])", "$1 $2");
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private static string InferDataType(string field) => field switch
    {
        var f when f.EndsWith("At", StringComparison.Ordinal) => "datetime",
        var f when f.EndsWith("AtUtc", StringComparison.Ordinal) => "datetime",
        var f when f.EndsWith("Date", StringComparison.Ordinal) => "date",
        var f when f.Contains("Count", StringComparison.Ordinal) => "number",
        var f when f.Contains("Bytes", StringComparison.Ordinal) => "number",
        var f when f.Contains("Size", StringComparison.Ordinal) => "number",
        var f when f.Contains("Memory", StringComparison.Ordinal) => "number",
        var f when f.Contains("Percent", StringComparison.Ordinal) => "number",
        var f when f.Contains("Cores", StringComparison.Ordinal) => "number",
        var f when f.Contains("Threads", StringComparison.Ordinal) => "number",
        var f when f.Contains("Frequency", StringComparison.Ordinal) => "number",
        var f when f.Contains("Tdp", StringComparison.Ordinal) => "number",
        var f when f.Contains("Speed", StringComparison.Ordinal) => "number",
        var f when f.Contains("Port", StringComparison.Ordinal) => "number",
        var f when f.Contains("Ghz", StringComparison.Ordinal) => "number",
        var f when f.Contains("Level", StringComparison.Ordinal) => "number",
        var f when f.Contains("Mbps", StringComparison.Ordinal) => "number",
        var f when f.Contains("Threshold", StringComparison.Ordinal) => "number",
        var f when f.Contains("Value", StringComparison.Ordinal) => "number",
        var f when f.StartsWith("is", StringComparison.Ordinal) => "boolean",
        var f when f.StartsWith("has", StringComparison.Ordinal) => "boolean",
        var f when f.Contains("Enabled", StringComparison.Ordinal) => "boolean",
        var f when f.Contains("Breached", StringComparison.Ordinal) => "boolean",
        var f when f.Contains("Shared", StringComparison.Ordinal) => "boolean",
        var f when f.Contains("Default", StringComparison.Ordinal) => "boolean",
        var f when f.EndsWith("Id", StringComparison.Ordinal) => "guid",
        _ => "string"
    };

    private static string InferFilterType(string filter) => filter switch
    {
        "from" or "to" => "date",
        "severity" or "status" or "state" => "enum",
        "port" or "threshold" or "escalationLevel" => "number",
        var f when f.EndsWith("Id", StringComparison.Ordinal) => "guid",
        _ => "string"
    };
}