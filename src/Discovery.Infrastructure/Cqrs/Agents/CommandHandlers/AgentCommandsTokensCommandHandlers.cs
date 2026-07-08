using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class SendAgentCommandCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<SendAgentCommandCommand, Result<AgentCommandDto>>
{
    public async Task<Result<AgentCommandDto>> Handle(SendAgentCommandCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<AgentCommandDto>.Failure(Error.NotFound("Agent not found."));

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = Enum.Parse<Core.Enums.CommandType>(cmd.CommandType), Payload = cmd.Payload ?? string.Empty };
        var created = await dispatcher.DispatchAsync(command, ct);
        return Result<AgentCommandDto>.Success(new AgentCommandDto(created.Id, created.CommandType.ToString(), created.Status.ToString(), created.CreatedAt));
    }
}

public sealed class CreateAgentTokenCommandHandler(
    IAgentRepository agentRepo,
    IAgentAuthService authService
) : IRequestHandler<CreateAgentTokenCommand, Result<AgentTokenDto>>
{
    public async Task<Result<AgentTokenDto>> Handle(CreateAgentTokenCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<AgentTokenDto>.Failure(Error.NotFound("Agent not found."));

        var (token, _) = await authService.CreateTokenAsync(cmd.AgentId, cmd.Name);
        return Result<AgentTokenDto>.Success(new AgentTokenDto(token.Id, token.Description ?? cmd.Name, token.TokenPrefix ?? string.Empty, token.CreatedAt, token.ExpiresAt));
    }
}

public sealed class RevokeAgentTokenCommandHandler(
    IAgentAuthService authService
) : IRequestHandler<RevokeAgentTokenCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RevokeAgentTokenCommand cmd, CancellationToken ct)
    {
        await authService.RevokeTokenAsync(cmd.TokenId);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class RevokeAllAgentTokensCommandHandler(
    IAgentAuthService authService
) : IRequestHandler<RevokeAllAgentTokensCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RevokeAllAgentTokensCommand cmd, CancellationToken ct)
    {
        await authService.RevokeAllTokensAsync(cmd.AgentId);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}