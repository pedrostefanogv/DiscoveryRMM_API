using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Queries;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAgentCommandsQueryHandler(
    ICommandRepository commandRepo
) : IRequestHandler<GetAgentCommandsQuery, Result<IReadOnlyList<AgentCommandDto>>>
{
    public async Task<Result<IReadOnlyList<AgentCommandDto>>> Handle(GetAgentCommandsQuery q, CancellationToken ct)
    {
        var commands = await commandRepo.GetByAgentIdAsync(q.AgentId, q.Limit);
        var dtos = commands
            .Select(c => new AgentCommandDto(
                c.Id,
                c.CommandType.ToString(),
                c.Status.ToString(),
                c.CreatedAt,
                c.Result,
                c.ExitCode,
                c.ErrorMessage,
                c.CompletedAt))
            .ToList();
        return Result<IReadOnlyList<AgentCommandDto>>.Success(dtos);
    }
}

public sealed class GetAgentTokensQueryHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IScopeContext scopeContext,
    IAgentAuthService authService
) : IRequestHandler<GetAgentTokensQuery, Result<IReadOnlyList<AgentTokenDto>>>
{
    public async Task<Result<IReadOnlyList<AgentTokenDto>>> Handle(GetAgentTokensQuery q, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(q.AgentId);
        if (agent is null) return Result<IReadOnlyList<AgentTokenDto>>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.View);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return Result<IReadOnlyList<AgentTokenDto>>.Failure(Error.Forbidden("Você não tem acesso a este agente."));

        var tokens = await authService.GetTokensByAgentIdAsync(q.AgentId);
        var dtos = tokens.Select(t => new AgentTokenDto(t.Id, t.Description ?? string.Empty, t.TokenPrefix ?? string.Empty, t.CreatedAt, t.ExpiresAt)).ToList();
        return Result<IReadOnlyList<AgentTokenDto>>.Success(dtos);
    }
}