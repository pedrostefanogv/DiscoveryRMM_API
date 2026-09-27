using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAgentByIdQueryHandler(
    IAgentRepository agentRepo,
    IHeartbeatCacheService heartbeatCache,
    IConfigurationResolver configResolver,
    ISiteRepository siteRepo
) : IRequestHandler<GetAgentByIdQuery, Result<AgentDto>>
{
    public async Task<Result<AgentDto>> Handle(GetAgentByIdQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.Id);
        if (agent is null)
            return Result<AgentDto>.Failure(Error.NotFound("Agent not found."));

        var heartbeat = await heartbeatCache.GetHeartbeatAsync(agent.Id);
        AgentQueryHelper.ApplyRealtimeHeartbeat(agent, heartbeat);
        var grace = await AgentQueryHelper.GetOnlineGraceSecondsAsync(configResolver, agent.SiteId);
        AgentQueryHelper.ApplyEffectiveStatus(agent, grace);

        var dto = AgentQueryHelper.MapToDto(agent);

        // Popula o ClientId do agente (o site guarda a associação ao cliente).
        // Necessário para o frontend resolver a cadeia Cliente → Site → Hostname
        // na interface de acesso remoto.
        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is not null)
            dto = dto with { ClientId = site.ClientId };

        return Result<AgentDto>.Success(dto);
    }
}

public sealed class GetAgentsBySiteQueryHandler(
    IAgentRepository agentRepo,
    IHeartbeatCacheService heartbeatCache,
    IConfigurationResolver configResolver
) : IRequestHandler<GetAgentsBySiteQuery, Result<IReadOnlyList<AgentDto>>>
{
    public async Task<Result<IReadOnlyList<AgentDto>>> Handle(GetAgentsBySiteQuery q, CancellationToken ct)
    {
        var agents = (await agentRepo.GetBySiteIdAsync(q.SiteId)).ToList();
        var heartbeatByAgent = await AgentQueryHelper.GetHeartbeatSnapshotAsync(heartbeatCache, agents.Select(a => a.Id));
        var grace = await AgentQueryHelper.GetOnlineGraceSecondsAsync(configResolver, q.SiteId);

        var dtos = new List<AgentDto>(agents.Count);
        foreach (var agent in agents)
        {
            AgentQueryHelper.ApplyRealtimeHeartbeat(agent, heartbeatByAgent.GetValueOrDefault(agent.Id));
            AgentQueryHelper.ApplyEffectiveStatus(agent, grace);
            dtos.Add(AgentQueryHelper.MapToDto(agent));
        }
        return Result<IReadOnlyList<AgentDto>>.Success(dtos);
    }
}

public sealed class GetAgentsByClientQueryHandler(
    IAgentRepository agentRepo,
    IHeartbeatCacheService heartbeatCache,
    IConfigurationResolver configResolver
) : IRequestHandler<GetAgentsByClientQuery, Result<IReadOnlyList<AgentDto>>>
{
    public async Task<Result<IReadOnlyList<AgentDto>>> Handle(GetAgentsByClientQuery q, CancellationToken ct)
    {
        var agents = (await agentRepo.GetByClientIdAsync(q.ClientId)).ToList();
        var heartbeatByAgent = await AgentQueryHelper.GetHeartbeatSnapshotAsync(heartbeatCache, agents.Select(a => a.Id));
        var graceBySite = await AgentQueryHelper.GetOnlineGraceSecondsBySiteAsync(configResolver, agents.Select(a => a.SiteId).Distinct());

        var dtos = new List<AgentDto>(agents.Count);
        foreach (var agent in agents)
        {
            AgentQueryHelper.ApplyRealtimeHeartbeat(agent, heartbeatByAgent.GetValueOrDefault(agent.Id));
            AgentQueryHelper.ApplyEffectiveStatus(agent, graceBySite.GetValueOrDefault(agent.SiteId, 60));
            dtos.Add(AgentQueryHelper.MapToDto(agent));
        }
        return Result<IReadOnlyList<AgentDto>>.Success(dtos);
    }
}

/// <summary>
/// Lixeira de agentes: página de soft-deleted com o clientId resolvido.
/// Não aplica heartbeat/status online — um agente excluído é sempre offline.
/// </summary>
public sealed class GetDeletedAgentsQueryHandler(
    DiscoveryDbContext db,
    ISiteRepository siteRepo
) : IRequestHandler<GetDeletedAgentsQuery, Result<DeletedAgentsPageDto>>
{
    public async Task<Result<DeletedAgentsPageDto>> Handle(GetDeletedAgentsQuery q, CancellationToken ct)
    {
        var safePage = q.Page < 1 ? 1 : q.Page;
        var safePageSize = Math.Clamp(q.PageSize, 1, 200);

        var query = db.Agents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(agent => agent.DeletedAt != null);

        if (q.SiteId.HasValue)
            query = query.Where(agent => agent.SiteId == q.SiteId.Value);

        if (q.ClientId.HasValue)
        {
            var targetClientId = q.ClientId.Value;
            query = query.Where(agent => db.Sites.Any(site => site.Id == agent.SiteId && site.ClientId == targetClientId));
        }

        // Row-level security: usuário sem acesso global só enxerga clientes permitidos.
        if (!q.HasGlobalAccess)
        {
            var allowedClientIds = q.AllowedClientIds?.Distinct().ToArray() ?? [];
            if (allowedClientIds.Length == 0)
                return Result<DeletedAgentsPageDto>.Success(new DeletedAgentsPageDto([], 0, safePage, safePageSize));

            query = query.Where(agent =>
                db.Sites.Any(site => site.Id == agent.SiteId && allowedClientIds.Contains(site.ClientId)));
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToLowerInvariant();
            query = query.Where(agent =>
                agent.Hostname.ToLower().Contains(term) ||
                (agent.DisplayName != null && agent.DisplayName.ToLower().Contains(term)) ||
                (agent.LastIpAddress != null && agent.LastIpAddress.ToLower().Contains(term)) ||
                (agent.OperatingSystem != null && agent.OperatingSystem.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var agents = await query
            .OrderByDescending(agent => agent.DeletedAt)
            .ThenBy(agent => agent.Hostname)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        // Uma única consulta de sites (includeInactive: o site pode ter sido
        // desativado depois da exclusão do agente).
        var siteIds = agents.Select(a => a.SiteId).Distinct().ToList();
        var clientIdBySite = new Dictionary<Guid, Guid>();
        if (siteIds.Count > 0)
        {
            foreach (var site in await siteRepo.GetByIdsAsync(siteIds, includeInactive: true))
                clientIdBySite[site.Id] = site.ClientId;
        }

        var items = agents
            .Select(a => AgentQueryHelper.MapToDto(a) with
            {
                ClientId = clientIdBySite.GetValueOrDefault(a.SiteId),
                Status = "Offline",
                IsOnline = false,
                DeletedAt = a.DeletedAt
            })
            .ToList()
            .AsReadOnly();

        return Result<DeletedAgentsPageDto>.Success(new DeletedAgentsPageDto(items, total, safePage, safePageSize));
    }
}

public sealed class GetAgentCustomFieldsQueryHandler(
    IAgentRepository agentRepo,
    ICustomFieldService customFieldService
) : IRequestHandler<GetAgentCustomFieldsQuery, Result<IReadOnlyList<CustomFieldValueDto>>>
{
    public async Task<Result<IReadOnlyList<CustomFieldValueDto>>> Handle(GetAgentCustomFieldsQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null)
            return Result<IReadOnlyList<CustomFieldValueDto>>.Failure(Error.NotFound("Agent not found."));

        var values = await customFieldService.GetValuesAsync(CustomFieldScopeType.Agent, q.AgentId, q.IncludeSecrets, ct);
        var dtos = values.Select(v => new CustomFieldValueDto(v.DefinitionId, v.Name, v.Label, v.ValueJson)).ToList();
        return Result<IReadOnlyList<CustomFieldValueDto>>.Success(dtos);
    }
}

public sealed class UpsertAgentCustomFieldCommandHandler(
    IAgentRepository agentRepo,
    ICustomFieldService customFieldService
) : IRequestHandler<UpsertAgentCustomFieldCommand, Result<CustomFieldValueDto>>
{
    public async Task<Result<CustomFieldValueDto>> Handle(UpsertAgentCustomFieldCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<CustomFieldValueDto>.Failure(Error.NotFound("Agent not found."));

        try
        {
            var result = await customFieldService.UpsertValueAsync(
                new UpsertCustomFieldValueInput(cmd.DefinitionId, CustomFieldScopeType.Agent, cmd.AgentId, cmd.ValueJson, cmd.UpdatedBy ?? "api"), ct);
            return Result<CustomFieldValueDto>.Success(new CustomFieldValueDto(result.DefinitionId, result.Name, result.Label, result.ValueJson));
        }
        catch (InvalidOperationException ex)
        {
            return Result<CustomFieldValueDto>.Failure(Error.Validation("ValueJson", ex.Message));
        }
    }
}