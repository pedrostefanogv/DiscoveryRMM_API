using Meduza.Api.Hubs;
using Meduza.Core.DTOs;
using Meduza.Core.Enums;
using Meduza.Core.Interfaces;

namespace Meduza.Api.Services;

public class SyncInvalidationPublisher : ISyncInvalidationPublisher
{
    private readonly IAgentRepository _agentRepository;
    private readonly ISyncPingDispatchQueue _dispatchQueue;
    private readonly ILogger<SyncInvalidationPublisher> _logger;

    public SyncInvalidationPublisher(
        IAgentRepository agentRepository,
        ISyncPingDispatchQueue dispatchQueue,
        ILogger<SyncInvalidationPublisher> logger)
    {
        _agentRepository = agentRepository;
        _dispatchQueue = dispatchQueue;
        _logger = logger;
    }

    public Task PublishGlobalAsync(
        SyncResourceType resource,
        string reason,
        AppInstallationType? installationType = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        return PublishByScopeAsync(
            resource,
            AppApprovalScopeType.Global,
            null,
            reason,
            installationType,
            correlationId,
            cancellationToken);
    }

    public async Task PublishByScopeAsync(
        SyncResourceType resource,
        AppApprovalScopeType scopeType,
        Guid? scopeId,
        string reason,
        AppInstallationType? installationType = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var changedAtUtc = DateTime.UtcNow;
        var revision = BuildRevision(resource, installationType, changedAtUtc);
        var eventId = Guid.NewGuid();

        List<Guid> agentIds;
        try
        {
            agentIds = await ResolveAgentIdsAsync(scopeType, scopeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to resolve agents for sync invalidation. Resource={Resource}, ScopeType={ScopeType}, ScopeId={ScopeId}",
                resource,
                scopeType,
                scopeId);
            return;
        }

        if (agentIds.Count == 0)
        {
            _logger.LogDebug(
                "No agents resolved for sync invalidation. Resource={Resource}, ScopeType={ScopeType}, ScopeId={ScopeId}",
                resource,
                scopeType,
                scopeId);
            return;
        }

        foreach (var agentId in agentIds)
        {
            var ping = new SyncInvalidationPingDto
            {
                EventId = eventId,
                AgentId = agentId,
                Resource = resource,
                ScopeType = scopeType,
                ScopeId = scopeId,
                InstallationType = installationType,
                Revision = revision,
                Reason = reason,
                ChangedAtUtc = changedAtUtc,
                CorrelationId = correlationId
            };

            await _dispatchQueue.EnqueueAsync(ping, cancellationToken);
        }

        _logger.LogInformation(
            "Sync invalidation published. Resource={Resource}, ScopeType={ScopeType}, ScopeId={ScopeId}, AgentCount={AgentCount}, Revision={Revision}",
            resource,
            scopeType,
            scopeId,
            agentIds.Count,
            revision);
    }

    private async Task<List<Guid>> ResolveAgentIdsAsync(AppApprovalScopeType scopeType, Guid? scopeId)
    {
        return scopeType switch
        {
            AppApprovalScopeType.Global => (await _agentRepository.GetAllAsync()).Select(x => x.Id).ToList(),
            AppApprovalScopeType.Client when scopeId.HasValue => (await _agentRepository.GetByClientIdAsync(scopeId.Value)).Select(x => x.Id).ToList(),
            AppApprovalScopeType.Site when scopeId.HasValue => (await _agentRepository.GetBySiteIdAsync(scopeId.Value)).Select(x => x.Id).ToList(),
            AppApprovalScopeType.Agent when scopeId.HasValue => await ResolveSingleAgentAsync(scopeId.Value),
            _ => []
        };
    }

    private async Task<List<Guid>> ResolveSingleAgentAsync(Guid agentId)
    {
        var agent = await _agentRepository.GetByIdAsync(agentId);
        return agent is null ? [] : [agent.Id];
    }

    private static string BuildRevision(SyncResourceType resource, AppInstallationType? installationType, DateTime changedAtUtc)
    {
        var resourcePart = resource.ToString().ToLowerInvariant();
        var typePart = installationType?.ToString().ToLowerInvariant() ?? "all";
        return $"{resourcePart}:{typePart}:{new DateTimeOffset(changedAtUtc).ToUnixTimeMilliseconds()}";
    }
}
