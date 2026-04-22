using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;

namespace Discovery.Api.Services;

/// <summary>
/// Resolve o escopo de um AgentAlertDefinition em uma lista de agents,
/// cria o AgentCommand correspondente para cada um e os entrega via NATS + SignalR.
/// </summary>
public class AlertDispatchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAgentRepository _agentRepo;
    private readonly IAgentLabelRepository _labelRepo;
    private readonly IAgentAlertRepository _alertRepo;
    private readonly IAgentCommandDispatcher _commandDispatcher;
    private readonly ILogger<AlertDispatchService> _logger;

    public AlertDispatchService(
        IAgentRepository agentRepo,
        IAgentLabelRepository labelRepo,
        IAgentAlertRepository alertRepo,
        IAgentCommandDispatcher commandDispatcher,
        ILogger<AlertDispatchService> logger)
    {
        _agentRepo = agentRepo;
        _labelRepo = labelRepo;
        _alertRepo = alertRepo;
        _commandDispatcher = commandDispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Despacha o alerta para todos os agents do escopo configurado.
    /// </summary>
    public async Task DispatchAsync(AgentAlertDefinition alert, CancellationToken cancellationToken = default)
    {
        await _alertRepo.UpdateStatusAsync(alert.Id, AlertDefinitionStatus.Dispatching);

        var agentIds = await ResolveAgentIdsAsync(alert, cancellationToken);

        if (agentIds.Count == 0)
        {
            _logger.LogWarning("AlertDispatch {AlertId}: nenhum agent encontrado para o escopo {Scope}.", alert.Id, alert.ScopeType);
            await _alertRepo.UpdateStatusAsync(alert.Id, AlertDefinitionStatus.Dispatched, DateTime.UtcNow, 0);
            return;
        }

        var payload = BuildPayloadJson(alert);
        var dispatchCount = 0;

        foreach (var agentId in agentIds)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await DispatchToAgentAsync(agentId, alert.Id, payload, cancellationToken);
                dispatchCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AlertDispatch {AlertId}: falha ao despachar para agent {AgentId}.", alert.Id, agentId);
            }
        }

        await _alertRepo.UpdateStatusAsync(alert.Id, AlertDefinitionStatus.Dispatched, DateTime.UtcNow, dispatchCount);
        _logger.LogInformation("AlertDispatch {AlertId}: despachado para {Count} agents.", alert.Id, dispatchCount);
    }

    private async Task<IReadOnlyList<Guid>> ResolveAgentIdsAsync(AgentAlertDefinition alert, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        return alert.ScopeType switch
        {
            AlertScopeType.Agent when alert.ScopeAgentId.HasValue
                => [alert.ScopeAgentId.Value],

            AlertScopeType.Site when alert.ScopeSiteId.HasValue
                => (await _agentRepo.GetBySiteIdAsync(alert.ScopeSiteId.Value))
                    .Select(a => a.Id).ToList(),

            AlertScopeType.Client when alert.ScopeClientId.HasValue
                => (await _agentRepo.GetByClientIdAsync(alert.ScopeClientId.Value))
                    .Select(a => a.Id).ToList(),

            AlertScopeType.Label when !string.IsNullOrWhiteSpace(alert.ScopeLabelName)
                => await ResolveAgentsByLabelAsync(alert.ScopeLabelName),

            _ => []
        };
    }

    private async Task<IReadOnlyList<Guid>> ResolveAgentsByLabelAsync(string labelName)
    {
        // Consulta direta na tabela agent_labels filtrando pelo nome da label
        var allAgents = await _agentRepo.GetAllAsync();
        var agentIds = allAgents.Select(a => a.Id).ToList();
        if (agentIds.Count == 0) return [];

        var labels = await _labelRepo.GetByAgentIdsAsync(agentIds);
        return labels
            .Where(l => string.Equals(l.Label, labelName, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.AgentId)
            .Distinct()
            .ToList();
    }

    private async Task DispatchToAgentAsync(Guid agentId, Guid alertId, string payloadJson, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var command = new AgentCommand
        {
            AgentId = agentId,
            CommandType = CommandType.ShowPsadtAlert,
            Payload = payloadJson
        };

        await _commandDispatcher.DispatchAsync(command, cancellationToken);
    }

    private static string BuildPayloadJson(AgentAlertDefinition alert)
    {
        object payload = alert.AlertType == PsadtAlertType.Toast
            ? new
            {
                alertId = alert.Id,
                type = "toast",
                title = alert.Title,
                message = alert.Message,
                timeoutSeconds = alert.TimeoutSeconds ?? 15,
                icon = alert.Icon
            }
            : new
            {
                alertId = alert.Id,
                type = "modal",
                title = alert.Title,
                message = alert.Message,
                actions = alert.ActionsJson != null
                    ? JsonSerializer.Deserialize<object>(alert.ActionsJson, JsonOptions)
                    : null,
                defaultAction = alert.DefaultAction,
                icon = alert.Icon
            };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
