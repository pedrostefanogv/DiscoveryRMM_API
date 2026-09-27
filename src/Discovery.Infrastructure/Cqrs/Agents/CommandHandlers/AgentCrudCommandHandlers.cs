using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class ApproveZeroTouchCommandHandler(
    IAgentRepository agentRepo,
    IAgentMessaging messaging
) : IRequestHandler<ApproveZeroTouchCommand, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(ApproveZeroTouchCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<AgentDto>.Failure(Error.NotFound("Agent not found."));
        if (!agent.ZeroTouchPending)
            return Result<AgentDto>.Failure(Error.Validation("AgentId", "Agent is not pending zero-touch approval."));

        await agentRepo.ApproveZeroTouchAsync(cmd.AgentId);

        var ping = new SyncInvalidationPingDto
        {
            EventId = Guid.NewGuid(),
            AgentId = cmd.AgentId,
            Resource = SyncResourceType.ZeroTouchApproved,
            ScopeType = AppApprovalScopeType.Agent,
            ScopeId = cmd.AgentId,
            Revision = $"zero-touch:{DateTime.UtcNow:O}",
            Reason = "zero-touch-approved",
            ChangedAtUtc = DateTime.UtcNow
        };
        await messaging.PublishSyncPingAsync(cmd.AgentId, SyncInvalidationPingMessage.FromDto(ping), ct);

        return Result<AgentDto>.Success(MapToDto(agent));
    }

    private static AgentDto MapToDto(Agent a) => AgentQueryHelper.MapToDto(a);
}

public sealed class CreateAgentCommandHandler(
    IAgentRepository agentRepo,
    IRedisService redis,
    ISiteRepository siteRepo
) : IRequestHandler<CreateAgentCommand, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(CreateAgentCommand cmd, CancellationToken ct)
    {
        var agent = new Agent
        {
            SiteId = cmd.SiteId,
            Hostname = cmd.Name,
            DisplayName = cmd.Name,
            MacAddress = cmd.MacAddress
        };
        var created = await agentRepo.CreateAsync(agent);
        await InvalidateCachesAsync(redis, siteRepo, created.SiteId, null);
        return Result<AgentDto>.Success(MapToDto(created));
    }

    internal static async Task InvalidateCachesAsync(IRedisService redis, ISiteRepository siteRepo, Guid currentSiteId, Guid? previousSiteId, Guid? agentId = null)
    {
        await redis.DeleteAsync("agents:all-ids");
        await redis.DeleteByPrefixAsync("software-inventory:");
        var siteIds = new HashSet<Guid> { currentSiteId };
        if (previousSiteId.HasValue) siteIds.Add(previousSiteId.Value);
        foreach (var siteId in siteIds)
        {
            await redis.DeleteAsync($"agents:by-site:{siteId:N}");
            var site = await siteRepo.GetByIdAsync(siteId);
            if (site is not null)
                await redis.DeleteAsync($"agents:by-client:{site.ClientId:N}");
        }
        if (agentId.HasValue)
        {
            await redis.DeleteAsync($"agents:single:{agentId.Value:N}");
            await redis.DeleteAsync($"agents:hardware:{agentId.Value:N}");
            await redis.DeleteAsync($"agents:software:snapshot:{agentId.Value:N}");
        }
    }

    internal static AgentDto MapToDto(Agent a) => AgentQueryHelper.MapToDto(a);
}

public sealed class UpdateAgentCommandHandler(
    IAgentRepository agentRepo,
    IRedisService redis,
    ISiteRepository siteRepo
) : IRequestHandler<UpdateAgentCommand, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(UpdateAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.Id);
        if (agent is null)
            return Result<AgentDto>.Failure(Error.NotFound("Agent not found."));

        var previousSiteId = agent.SiteId;
        if (cmd.SiteId.HasValue) agent.SiteId = cmd.SiteId.Value;
        if (cmd.Name is not null) { agent.Hostname = cmd.Name; agent.DisplayName = cmd.Name; }
        if (cmd.MacAddress is not null) agent.MacAddress = cmd.MacAddress;

        await agentRepo.UpdateAsync(agent);
        await CreateAgentCommandHandler.InvalidateCachesAsync(redis, siteRepo, agent.SiteId, previousSiteId);
        return Result<AgentDto>.Success(CreateAgentCommandHandler.MapToDto(agent));
    }
}

public sealed class DeleteAgentCommandHandler(
    IAgentRepository agentRepo,
    IAgentAuthService authService,
    IRedisService redis,
    ISiteRepository siteRepo
) : IRequestHandler<DeleteAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeleteAgentCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.Id);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        await authService.RevokeAllTokensAsync(cmd.Id);
        await agentRepo.DeleteAsync(cmd.Id);
        await CreateAgentCommandHandler.InvalidateCachesAsync(redis, siteRepo, agent.SiteId, null, cmd.Id);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

/// <summary>
/// Exclusão definitiva (hard delete) do agente na lixeira. Sem <c>Force</c>,
/// recusa quando o agente tem chamados vinculados (409 com a contagem); com
/// <c>Force</c>, o histórico é preservado desvinculado (<c>agent_id = NULL</c>).
/// </summary>
public sealed class PurgeAgentCommandHandler(
    DiscoveryDbContext db,
    IAgentPurgeService purgeService,
    IRedisService redis,
    ISiteRepository siteRepo,
    ILogger<PurgeAgentCommandHandler> logger
) : IRequestHandler<PurgeAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(PurgeAgentCommand cmd, CancellationToken ct)
    {
        var agent = await db.Agents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(a => a.Id == cmd.Id, ct);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        // Exclusão definitiva é o segundo passo: o agente precisa estar na lixeira.
        if (agent.DeletedAt is null)
            return Result<VoidResult>.Failure(Error.Conflict(
                "Mova o agente para a lixeira antes de excluir definitivamente."));

        if (!cmd.Force)
        {
            // Inclui chamados já excluídos (lixeira) para não subestimar o vínculo.
            var linkedTickets = await db.Tickets
                .IgnoreQueryFilters()
                .CountAsync(ticket => ticket.AgentId == cmd.Id, ct);
            if (linkedTickets > 0)
                return Result<VoidResult>.Failure(Error.Conflict(
                    $"Agente vinculado a {linkedTickets} chamado(s). Confirme a exclusão definitiva para continuar."));
        }

        try
        {
            await purgeService.PurgeAsync(cmd.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // FK inesperada (tabela nova referenciando agents) ou indisponibilidade
            // do banco: nada foi commitado. O detalhe vai para o log; o cliente
            // recebe mensagem genérica (evita expor nomes de tabela/constraint).
            logger.LogError(ex, "Falha ao excluir definitivamente o agente {AgentId}", cmd.Id);
            return Result<VoidResult>.Failure(Error.Internal(
                "Não foi possível excluir o agente definitivamente. Nenhuma alteração foi aplicada."));
        }

        try
        {
            await CreateAgentCommandHandler.InvalidateCachesAsync(redis, siteRepo, agent.SiteId, null, cmd.Id);
        }
        catch
        {
            // O agente já foi removido; a limpeza de cache é best-effort (TTL curto).
        }

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

/// <summary>Tira o agente da lixeira (limpa DeletedAt). Idempotente.</summary>
public sealed class RestoreAgentCommandHandler(
    DiscoveryDbContext db,
    IRedisService redis,
    ISiteRepository siteRepo
) : IRequestHandler<RestoreAgentCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RestoreAgentCommand cmd, CancellationToken ct)
    {
        var agent = await db.Agents
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(a => a.Id == cmd.Id, ct);
        if (agent is null)
            return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        // Idempotente: agente fora da lixeira já está restaurado.
        if (agent.DeletedAt is not null)
        {
            agent.DeletedAt = null;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await CreateAgentCommandHandler.InvalidateCachesAsync(redis, siteRepo, agent.SiteId, null, cmd.Id);
        }

        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
