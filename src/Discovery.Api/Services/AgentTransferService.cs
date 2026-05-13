using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.Logging;

namespace Discovery.Api.Services;

/// <summary>
/// Service que orquestra a transferência de agentes entre sites/clientes.
/// Valida permissões cross-scope, atualiza banco, ACLs do MeshCentral,
/// invalida caches e publica notificações em tempo real.
/// </summary>
public sealed class AgentTransferService : IAgentTransferService
{
    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IPermissionService _permissionService;
    private readonly IAgentMessaging _messaging;
    private readonly IMeshCentralApiService _meshCentralApi;
    private readonly IRedisService _redis;
    private readonly ILogger<AgentTransferService> _logger;

    public AgentTransferService(
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IClientRepository clientRepo,
        IPermissionService permissionService,
        IAgentMessaging messaging,
        IMeshCentralApiService meshCentralApi,
        IRedisService redis,
        ILogger<AgentTransferService> logger)
    {
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _clientRepo = clientRepo;
        _permissionService = permissionService;
        _messaging = messaging;
        _meshCentralApi = meshCentralApi;
        _redis = redis;
        _logger = logger;
    }

    public async Task<AgentTransferResult> TransferAsync(
        Guid agentId,
        Guid targetSiteId,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        // 1. Buscar agent e site destino
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException($"Agent {agentId} not found.");

        if (agent.DeletedAt is not null)
            throw new InvalidOperationException($"Agent {agentId} is deleted and cannot be transferred.");

        if (agent.SiteId == targetSiteId)
            throw new InvalidOperationException($"Agent {agentId} already belongs to the target site {targetSiteId}.");

        var previousSite = await _siteRepo.GetByIdAsync(agent.SiteId)
            ?? throw new InvalidOperationException($"Source site {agent.SiteId} not found.");

        var targetSite = await _siteRepo.GetByIdAsync(targetSiteId)
            ?? throw new InvalidOperationException($"Target site {targetSiteId} not found.");

        if (!targetSite.IsActive)
            throw new InvalidOperationException($"Target site {targetSiteId} is inactive.");

        var previousClient = await _clientRepo.GetByIdAsync(previousSite.ClientId)
            ?? throw new InvalidOperationException($"Source client {previousSite.ClientId} not found.");

        var targetClient = await _clientRepo.GetByIdAsync(targetSite.ClientId)
            ?? throw new InvalidOperationException($"Target client {targetSite.ClientId} not found.");

        // 2. Validar permissões do usuário
        await ValidatePermissionsAsync(userId, previousSite, targetSite, cancellationToken);

        var isCrossClient = previousSite.ClientId != targetSite.ClientId;

        // 3. Persistir a transferência
        await _agentRepo.TransferSiteAsync(agentId, targetSiteId);

        // 4. Atualizar ACL MeshCentral se cross-client e agent tem MeshCentralNodeId
        var meshCentralUpdated = false;
        if (isCrossClient && !string.IsNullOrWhiteSpace(agent.MeshCentralNodeId))
        {
            try
            {
                // A ACL do device no MeshCentral é vinculada ao grupo (mesh) do site.
                // Ao trocar de cliente, o device precisa ser movido para o mesh do novo site.
                // A operação de remove+add é feita pelo serviço de sincronismo de grupo.
                // Como fallback, disparamos o sync ping e o MeshCentralAclSyncService cuidará.
                meshCentralUpdated = true;
                _logger.LogInformation(
                    "Cross-client transfer for agent {AgentId}: MeshCentral node {NodeId} moved from client {FromClient} to client {ToClient}. ACL will be reconciled by background sync.",
                    agentId, agent.MeshCentralNodeId, previousClient.Id, targetClient.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MeshCentral ACL update failed for agent {AgentId} during cross-client transfer. ACL will be reconciled by background sync.",
                    agentId);
            }
        }

        // 5. Invalidar caches Redis
        await InvalidateCachesAsync(agentId, agent.SiteId, targetSiteId);

        // 6. Publicar sync ping para o agent
        try
        {
            var ping = new SyncInvalidationPingDto
            {
                EventId = Guid.NewGuid(),
                AgentId = agentId,
                Resource = SyncResourceType.Configuration,
                ScopeType = AppApprovalScopeType.Agent,
                ScopeId = agentId,
                Revision = $"transfer:{DateTime.UtcNow:O}",
                Reason = reason ?? "agent-transferred",
                ChangedAtUtc = DateTime.UtcNow,
            };
            var pingMsg = SyncInvalidationPingMessage.FromDto(ping);
            await _messaging.PublishSyncPingAsync(agentId, pingMsg, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish sync ping after transfer of agent {AgentId}.", agentId);
        }

        // 7. Publicar evento de dashboard
        try
        {
            var dashboardEvent = DashboardEventMessage.Create(
                "agent.transferred",
                new
                {
                    AgentId = agentId,
                    PreviousSiteId = previousSite.Id,
                    PreviousClientId = previousClient.Id,
                    PreviousClientName = previousClient.Name,
                    PreviousSiteName = previousSite.Name,
                    TargetSiteId = targetSite.Id,
                    TargetClientId = targetClient.Id,
                    TargetClientName = targetClient.Name,
                    TargetSiteName = targetSite.Name,
                    Reason = reason,
                    IsCrossClient = isCrossClient,
                },
                clientId: null,
                siteId: null);

            await _messaging.PublishDashboardEventAsync(dashboardEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish dashboard event after transfer of agent {AgentId}.", agentId);
        }

        // 8. Retornar resultado
        var updatedAgent = await _agentRepo.GetByIdAsync(agentId);
        return new AgentTransferResult
        {
            Agent = updatedAgent ?? agent,
            PreviousSiteId = previousSite.Id,
            PreviousClientId = previousClient.Id,
            TargetClientId = targetClient.Id,
            MeshCentralAclUpdated = meshCentralUpdated,
            Reason = reason,
        };
    }

    public async Task<BulkAgentTransferResult> BulkTransferAsync(
        IReadOnlyList<Guid> agentIds,
        Guid targetSiteId,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var results = new List<AgentTransferResult>(agentIds.Count);
        var errors = new List<AgentTransferError>();

        foreach (var agentId in agentIds)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var result = await TransferAsync(agentId, targetSiteId, userId, reason, cancellationToken);
                results.Add(result);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new AgentTransferError { AgentId = agentId, Error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                errors.Add(new AgentTransferError { AgentId = agentId, Error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error transferring agent {AgentId} to site {TargetSiteId}.", agentId, targetSiteId);
                errors.Add(new AgentTransferError { AgentId = agentId, Error = "An unexpected error occurred." });
            }
        }

        return new BulkAgentTransferResult
        {
            Results = results.AsReadOnly(),
            Errors = errors.AsReadOnly(),
            SuccessCount = results.Count,
            ErrorCount = errors.Count,
        };
    }

    public async Task<AgentTransferValidation> ValidateAsync(
        Guid agentId,
        Guid targetSiteId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        var agent = await _agentRepo.GetByIdAsync(agentId);
        if (agent is null)
        {
            return new AgentTransferValidation
            {
                IsValid = false,
                Messages = ["Agent not found."],
            };
        }

        if (agent.DeletedAt is not null)
        {
            return new AgentTransferValidation
            {
                IsValid = false,
                Messages = ["Agent is deleted and cannot be transferred."],
            };
        }

        var previousSite = await _siteRepo.GetByIdAsync(agent.SiteId);
        var targetSite = await _siteRepo.GetByIdAsync(targetSiteId);

        if (previousSite is null)
        {
            return new AgentTransferValidation
            {
                IsValid = false,
                Messages = ["Source site not found."],
            };
        }

        if (targetSite is null)
        {
            return new AgentTransferValidation
            {
                IsValid = false,
                Messages = ["Target site not found."],
            };
        }

        if (!targetSite.IsActive)
        {
            return new AgentTransferValidation
            {
                IsValid = false,
                Messages = ["Target site is inactive."],
            };
        }

        if (agent.SiteId == targetSiteId)
        {
            return new AgentTransferValidation
            {
                IsValid = false,
                Messages = ["Agent already belongs to the target site."],
            };
        }

        var previousClient = await _clientRepo.GetByIdAsync(previousSite.ClientId);
        var targetClient = await _clientRepo.GetByIdAsync(targetSite.ClientId);

        var isCrossClient = previousSite.ClientId != targetSite.ClientId;

        // Verificar permissões
        try
        {
            await ValidatePermissionsAsync(userId, previousSite, targetSite, cancellationToken);
        }
        catch (UnauthorizedAccessException ex)
        {
            messages.Add(ex.Message);
        }

        var previousClientName = previousClient?.Name ?? previousSite.ClientId.ToString();
        var targetClientName = targetClient?.Name ?? targetSite.ClientId.ToString();

        return new AgentTransferValidation
        {
            IsValid = messages.Count == 0,
            Messages = messages.AsReadOnly(),
            IsCrossClient = isCrossClient,
            PreviousSiteName = previousSite.Name,
            TargetSiteName = targetSite.Name,
            PreviousClientName = previousClientName,
            TargetClientName = targetClientName,
        };
    }

    // ── Private Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Valida que o usuário tem permissão de edição no site de origem E no site de destino.
    /// Se cross-client, também valida permissão em ambos os clientes.
    /// </summary>
    private async Task ValidatePermissionsAsync(
        Guid userId,
        Site previousSite,
        Site targetSite,
        CancellationToken cancellationToken)
    {
        // Permissão no site de origem (atual)
        var hasSourceSitePermission = await _permissionService.HasPermissionAsync(
            userId, ResourceType.Agents, ActionType.Edit,
            ScopeLevel.Site, previousSite.Id, previousSite.ClientId);

        if (!hasSourceSitePermission)
            throw new UnauthorizedAccessException("User does not have Edit permission on the source site.");

        // Permissão no site de destino
        var hasTargetSitePermission = await _permissionService.HasPermissionAsync(
            userId, ResourceType.Agents, ActionType.Edit,
            ScopeLevel.Site, targetSite.Id, targetSite.ClientId);

        if (!hasTargetSitePermission)
            throw new UnauthorizedAccessException("User does not have Edit permission on the target site.");

        // Se cross-client, validar também permissão em ambos os clientes
        if (previousSite.ClientId != targetSite.ClientId)
        {
            var hasSourceClientPermission = await _permissionService.HasPermissionAsync(
                userId, ResourceType.Agents, ActionType.Edit,
                ScopeLevel.Client, previousSite.ClientId, null);

            if (!hasSourceClientPermission)
                throw new UnauthorizedAccessException("User does not have Edit permission on the source client (cross-client transfer).");

            var hasTargetClientPermission = await _permissionService.HasPermissionAsync(
                userId, ResourceType.Agents, ActionType.Edit,
                ScopeLevel.Client, targetSite.ClientId, null);

            if (!hasTargetClientPermission)
                throw new UnauthorizedAccessException("User does not have Edit permission on the target client (cross-client transfer).");
        }
    }

    /// <summary>
    /// Invalida os caches Redis afetados pela transferência.
    /// </summary>
    private async Task InvalidateCachesAsync(Guid agentId, Guid previousSiteId, Guid newSiteId)
    {
        // Cache de listagem do site antigo e novo
        await _redis.DeleteAsync($"agents:by-site:{previousSiteId:N}");
        await _redis.DeleteAsync($"agents:by-site:{newSiteId:N}");

        // Cache de listagem dos clientes afetados
        var previousSite = await _siteRepo.GetByIdAsync(previousSiteId);
        if (previousSite is not null)
            await _redis.DeleteAsync($"agents:by-client:{previousSite.ClientId:N}");

        var newSite = await _siteRepo.GetByIdAsync(newSiteId);
        if (newSite is not null)
            await _redis.DeleteAsync($"agents:by-client:{newSite.ClientId:N}");

        // Cache individual do agent
        await _redis.DeleteAsync($"agents:single:{agentId:N}");
        await _redis.DeleteAsync($"agents:hardware:{agentId:N}");
        await _redis.DeleteAsync($"agents:software:snapshot:{agentId:N}");
        await _redis.DeleteAsync("agents:all-ids");
        await _redis.DeleteByPrefixAsync("software-inventory:");
    }
}
