using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Queries;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

public sealed class GetAgentCommandsQueryHandler(
    ICommandRepository commandRepo
) : IRequestHandler<GetAgentCommandsQuery, Result<IReadOnlyList<AgentCommandDto>>>
{
    public async Task<Result<IReadOnlyList<AgentCommandDto>>> Handle(GetAgentCommandsQuery q, CancellationToken ct)
    {
        var commands = await commandRepo.GetByAgentIdAsync(q.AgentId, q.Limit);
        var dtos = commands.Select(c => new AgentCommandDto(c.Id, c.CommandType.ToString(), c.Status.ToString(), c.CreatedAt)).ToList();
        return Result<IReadOnlyList<AgentCommandDto>>.Success(dtos);
    }
}

public sealed class GetAgentTokensQueryHandler(
    IAgentAuthService authService
) : IRequestHandler<GetAgentTokensQuery, Result<IReadOnlyList<AgentTokenDto>>>
{
    public async Task<Result<IReadOnlyList<AgentTokenDto>>> Handle(GetAgentTokensQuery q, CancellationToken ct)
    {
        var tokens = await authService.GetTokensByAgentIdAsync(q.AgentId);
        var dtos = tokens.Select(t => new AgentTokenDto(t.Id, t.Description ?? string.Empty, t.TokenPrefix ?? string.Empty, t.CreatedAt, t.ExpiresAt)).ToList();
        return Result<IReadOnlyList<AgentTokenDto>>.Success(dtos);
    }
}