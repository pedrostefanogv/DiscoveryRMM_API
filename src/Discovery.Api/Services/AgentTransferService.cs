using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Discovery.Api.Services;

/// <summary>
/// Service que orquestra a transferência de agentes entre sites/clientes.
/// Valida permissões cross-scope, atualiza banco,
/// invalida caches e publica notificações em tempo real.
/// </summary>
public sealed class AgentTransferService : IAgentTransferService
{
    private readonly IAgentRepository _agentRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IClientRepository _clientRepo;
    private readonly IPermissionService _permissionService;
    private readonly IAgentMessaging _messaging;
    private readonly IRedisService _redis;
    private readonly ISyncPingDeliveryRepository _syncPingDeliveryRepo;
    private readonly ILogger<AgentTransferService> _logger;

    public AgentTransferService(
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IClientRepository clientRepo,
        IPermissionService permissionService,
        IAgentMessaging messaging,
        IRedisService redis,
        ISyncPingDeliveryRepository syncPingDeliveryRepo,
        ILogger<AgentTransferService> logger)
    {
        _agentRepo = agentRepo;
        _siteRepo = siteRepo;
        _clientRepo = clientRepo;
        _permissionService = permissionService;
        _messaging = messaging;
        _redis = redis;
        _syncPingDeliveryRepo = syncPingDeliveryRepo;
        _logger = logger;
    }

    public async Task<AgentTransferResult> TransferAsync(
        Guid agentId,
        Guid targetSiteId,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        // Usuário vazio nunca tem atribuição de role: a checagem de permissão negaria
        // qualquer transferência por um motivo enganoso. Falha explícita em vez disso.
        if (userId == Guid.Empty)
            throw new UnauthorizedAccessException("User not authenticated.");

        // 1. Buscar agent e site destino
        var agent = await _agentRepo.GetByIdAsync(agentId)
            ?? throw new InvalidOperationException($"Agent {agentId} not found.");

        if (agent.DeletedAt is not null)
            throw new InvalidOperationException($"Agent {agentId} is deleted and cannot be transferred.");

        // Já está no destino: retorno idempotente. Antes lançava InvalidOperationException,
        // então uma transferência que persistiu mas falhou ao notificar aparecia como erro
        // no front e a retentativa reclamava de "already belongs to the target site".
        if (agent.SiteId == targetSiteId)
            return await BuildIdempotentResultAsync(agent, targetSiteId, reason, cancellationToken);

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

        // 4. Invalidar caches Redis. Falha aqui não pode desfazer a transferência já
        //    persistida; fica registrada para investigação (dados ficam corretos no banco).
        try
        {
            await InvalidateCachesAsync(agentId, agent.SiteId, targetSiteId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao invalidar caches após transferência do agent {AgentId} ({PreviousSiteId} -> {TargetSiteId}).",
                agentId, agent.SiteId, targetSiteId);
        }

        // 5. Notificar o agent — dual-publish + comando de reconnect (Fase 1/2 do plano
        //    AGENT_TRANSFER_SYNC_FIX_PLAN).
        //    O agent ainda está subscrito (JWT/ACL NATS) nos subjects do site ANTIGO;
        //    publicar apenas no novo faria a notificação se perder silenciosamente.
        //    Isolado: se a notificação falhar, a transferência já está persistida e o
        //    agent se recupera pelo polling de configuração.
        var notified = false;
        try
        {
            notified = await NotifyAgentAfterTransferAsync(
                agentId, previousSite, targetSite, reason, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Falha ao notificar o agent {AgentId} após transferência; recuperação via polling de config.",
                agentId);
        }

        // 7. Publicar evento de dashboard
        try
        {
            var dashboardEvent = DashboardEventMessage.Create(
                "AgentTransferred",
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
            Reason = reason,
            AgentNotified = notified,
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

        // Sem usuário não há permissão a avaliar: devolve erro claro em vez do enganoso
        // "User does not have Edit permission on the source site.".
        if (userId == Guid.Empty)
        {
            return new AgentTransferValidation
            {
                IsValid = false,
                Messages = ["User not authenticated."],
            };
        }

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
    /// Resultado devolvido quando o agent já está no site de destino. Sem evento novo nem
    /// notificação: a transferência já aconteceu (ex.: persistiu e falhou ao notificar),
    /// então a retentativa deve ser um no-op bem-sucedido para o operador/UI.
    /// </summary>
    private async Task<AgentTransferResult> BuildIdempotentResultAsync(
        Agent agent,
        Guid targetSiteId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var targetSite = await _siteRepo.GetByIdAsync(targetSiteId);
        var targetClientId = targetSite?.ClientId ?? Guid.Empty;

        _logger.LogInformation(
            "Transferência do agent {AgentId} para {TargetSiteId} ignorada: já está no destino.",
            agent.Id, targetSiteId);

        return new AgentTransferResult
        {
            Agent = agent,
            PreviousSiteId = agent.SiteId,
            PreviousClientId = targetClientId,
            TargetClientId = targetClientId,
            Reason = reason,
            AgentNotified = false,
        };
    }

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
    /// Notifica o agent após a transferência: dual-publish do sync ping (subjects antigo
    /// e novo), persistência do delivery para auditoria e comando nats.reconnect no
    /// subject antigo para forçar re-auth imediata (JWT com subjects do site novo).
    /// </summary>
    private async Task<bool> NotifyAgentAfterTransferAsync(
        Guid agentId,
        Site previousSite,
        Site targetSite,
        string? reason,
        CancellationToken cancellationToken)
    {
        var notified = false;
        var revision = $"transfer:{DateTime.UtcNow:O}";

        try
        {
            var ping = new SyncInvalidationPingDto
            {
                EventId = Guid.NewGuid(),
                AgentId = agentId,
                Resource = SyncResourceType.Configuration,
                ScopeType = AppApprovalScopeType.Agent,
                ScopeId = agentId,
                Revision = revision,
                Reason = reason ?? "agent-transferred",
                ChangedAtUtc = DateTime.UtcNow,
            };
            var pingMsg = SyncInvalidationPingMessage.FromDto(ping);

            // a) Subject ANTIGO — único canal garantido (agent ainda está subscrito nele).
            await _messaging.PublishSyncPingAsync(
                agentId, pingMsg, previousSite.ClientId, previousSite.Id, cancellationToken);
            notified = true;

            // b) Subject NOVO — cobre agents que já reconectaram (best-effort).
            await _messaging.PublishSyncPingAsync(
                agentId, pingMsg, targetSite.ClientId, targetSite.Id, cancellationToken);

            // c) Persistência do delivery para auditoria/retry (padrão do
            //    SyncPingDispatchBackgroundService).
            try
            {
                await _syncPingDeliveryRepo.CreateSentAsync(
                    ping.EventId, agentId, ping.Resource, ping.Revision);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to persist sync ping delivery for transferred agent {AgentId}.",
                    agentId);
            }

            // d) Comando nats.reconnect no subject ANTIGO: força o agent a re-buscar a
            //    config e reconectar ao NATS, recebendo JWT com os subjects do site novo
            //    via auth callout — fecha a janela de quebra em segundos em vez de esperar
            //    o TTL do JWT.
            try
            {
                var reconnectPayload = JsonSerializer.Serialize(new
                {
                    version = 1,
                    reason = "agent-transferred",
                    newSiteId = targetSite.Id,
                    newClientId = targetSite.ClientId,
                    revision,
                });

                await _messaging.SendCommandToSubjectAsync(
                    previousSite.ClientId,
                    previousSite.Id,
                    agentId,
                    Guid.NewGuid(),
                    "nats.reconnect",
                    reconnectPayload);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to send nats.reconnect command to transferred agent {AgentId} on previous site subject.",
                    agentId);
            }

            _logger.LogInformation(
                "Agent {AgentId} transferred: sync ping dual-published (old site {PreviousSiteId}, new site {TargetSiteId}) and nats.reconnect sent. Notified={Notified}",
                agentId, previousSite.Id, targetSite.Id, notified);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to notify agent {AgentId} after transfer. Auto-recovery relies on agent config polling.",
                agentId);
        }

        return notified;
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
