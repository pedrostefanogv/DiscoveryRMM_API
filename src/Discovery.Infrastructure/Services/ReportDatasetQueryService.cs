using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

public partial class ReportDatasetQueryService : IReportDatasetQueryService
{
    private const int DefaultLimit = 1000;
    private const int MaxLimit = 10000;
    private const int HardLimit = 100_000;

    private readonly DiscoveryDbContext _db;
    private readonly IMemoryCache _cache;
    /// <summary>
    /// Logger OPCIONAL: existe ctor de 2 args que passa null de proposito, entao
    /// TODA chamada precisa ser null-conditional. Logar e best-effort — nunca pode
    /// mascarar o erro de negocio que a execucao esta prestes a lancar.
    /// </summary>
    private readonly ILogger<ReportDatasetQueryService>? _logger;
    private static readonly TimeSpan QueryCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Resultados maiores que isso nao entram no cache: um cache sem SizeLimit
    /// guardando ate 10.000 linhas por dataset/filtro crescia sem teto.
    /// </summary>
    private const int QueryCacheMaxRows = 2_000;

    public ReportDatasetQueryService(DiscoveryDbContext db, IMemoryCache cache)
        : this(db, cache, null)
    {
    }

    public ReportDatasetQueryService(DiscoveryDbContext db, IMemoryCache cache, ILogger<ReportDatasetQueryService>? logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ReportQueryResult> QueryAsync(ReportTemplate template, string? filtersJson, CancellationToken cancellationToken = default)
    {
        var layout = ReportLayoutDefinitionParser.ParseOrDefault(template.LayoutJson);
        if (layout.DataSources is { Count: > 1 })
            return await QueryMultiSourceAsync(layout.DataSources, filtersJson, cancellationToken);

        var filters = ParseFilters(filtersJson);
        var clientId = GetGuid(filters, "clientId");

        return await QuerySingleDatasetAsync(template.DatasetType, clientId, filters, cancellationToken);
    }

    private async Task<ReportQueryResult> QuerySingleDatasetAsync(ReportDatasetType datasetType, Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        return datasetType switch
        {
            ReportDatasetType.SoftwareInventory => await QuerySoftwareInventoryAsync(clientId, filters, cancellationToken),
            ReportDatasetType.Logs => await QueryLogsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.ConfigurationAudit => await QueryConfigurationAuditAsync(filters, cancellationToken),
            ReportDatasetType.Tickets => await QueryTicketsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentHardware => await QueryAgentHardwareAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentInventoryComposite => await QueryAgentInventoryCompositeAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentLabels => await QueryAgentLabelsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AutomaticLabelRules => await QueryAutomaticLabelRulesAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AutomationExecutions => await QueryAutomationExecutionsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentMonitoringEvents => await QueryAgentMonitoringEventsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentAlerts => await QueryAgentAlertsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentSensors => await QueryAgentSensorsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.P2pTelemetry => await QueryP2pTelemetryAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AgentDisks => await QueryAgentDisksAsync(clientId, filters, cancellationToken),
            ReportDatasetType.NetworkAdapters => await QueryNetworkAdaptersAsync(clientId, filters, cancellationToken),
            ReportDatasetType.ListeningPorts => await QueryListeningPortsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.Printers => await QueryPrintersAsync(clientId, filters, cancellationToken),
            ReportDatasetType.SoftwareCatalog => await QuerySoftwareCatalogAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AutomationScripts => await QueryAutomationScriptsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.AppPackages => await QueryAppPackagesAsync(clientId, filters, cancellationToken),
            ReportDatasetType.TicketActivity => await QueryTicketActivityAsync(clientId, filters, cancellationToken),
            ReportDatasetType.TicketEscalations => await QueryTicketEscalationsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.CustomFields => await QueryCustomFieldsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.TicketAnswers => await QueryTicketAnswersAsync(clientId, filters, cancellationToken),
            ReportDatasetType.KnowledgeBase => await QueryKnowledgeBaseAsync(clientId, filters, cancellationToken),
            ReportDatasetType.WindowsUpdates => await QueryWindowsUpdatesAsync(clientId, filters, cancellationToken),
            ReportDatasetType.PatchCompliance => await QueryPatchComplianceAsync(clientId, filters, cancellationToken),
            ReportDatasetType.PatchApprovals => await QueryPatchApprovalsAsync(clientId, filters, cancellationToken),
            ReportDatasetType.PatchApprovalAudit => await QueryPatchApprovalAuditAsync(clientId, filters, cancellationToken),
            ReportDatasetType.Agents => await QueryAgentsAsync(clientId, filters, cancellationToken),

            // Antes o fallback devolvia um resultado de SUCESSO com uma linha
            // "Dataset not supported.": o usuario recebia um arquivo inutil com
            // status Completed e notificacao de sucesso. Adicionar um dataset ao
            // enum sem o case aqui passava em silencio. Agora falha alto — o
            // ProcessExecutionAsync captura, grava a mensagem e notifica.
            _ => throw new InvalidOperationException(
                $"O dataset '{datasetType}' ({(int)datasetType}) nao tem consulta implementada em " +
                $"{nameof(ReportDatasetQueryService)}. Adicione o case em QuerySingleDatasetAsync.")
        };
    }
}
