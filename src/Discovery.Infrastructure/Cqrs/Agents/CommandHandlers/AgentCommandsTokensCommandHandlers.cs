using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Commands;
using Discovery.Core.Cqrs.Agents.CommandsTokens.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

public sealed class SendAgentCommandCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher,
    ISiteRepository siteRepo,
    IScopeContext scopeContext
) : IRequestHandler<SendAgentCommandCommand, Result<AgentCommandDto>>
{
    public async Task<Result<AgentCommandDto>> Handle(SendAgentCommandCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<AgentCommandDto>.Failure(Error.NotFound("Agent not found."));

        // Comandos só são permitidos no PRÓPRIO tenant: o endpoint usa AccessList
        // (aceita usuário escopado) e o recorte de tenant acontece aqui — um
        // técnico do cliente A não envia comando ao agente do cliente B.
        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        var access = await scopeContext.GetAccessAsync(ResourceType.Agents, ActionType.Execute);
        if (!TenantScopePolicy.Allows(access, site?.ClientId ?? Guid.Empty, agent.SiteId))
            return Result<AgentCommandDto>.Failure(Error.Forbidden("Você não tem acesso a este agente."));

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