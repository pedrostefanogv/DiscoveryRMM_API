using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Cqrs.Agents.Crud.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAgentByIdQueryHandler(
    IAgentRepository agentRepo,
    IHeartbeatCacheService heartbeatCache,
    IConfigurationResolver configResolver
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

        return Result<AgentDto>.Success(AgentQueryHelper.MapToDto(agent));
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