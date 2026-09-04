using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.P2p.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

/// <summary>
/// Despacha o comando de pré-carga P2P para um agent específico.
/// O agent decide quando baixar conforme o score de eleição local (o melhor
/// candidato baixa; os demais replicam via re-seed P2P).
/// </summary>
public sealed class RequestP2pPreloadCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<RequestP2pPreloadCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(RequestP2pPreloadCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null) return Result<VoidResult>.Failure(Error.NotFound("Agent not found."));

        var action = string.Equals(cmd.Action, "cancel", StringComparison.OrdinalIgnoreCase) ? "cancel" : "preload";
        var payload = JsonSerializer.Serialize(new
        {
            action,
            packages = cmd.Packages.Select(p => new { packageId = p.PackageId, actionType = p.ActionType })
        });

        var command = new AgentCommand { AgentId = cmd.AgentId, CommandType = CommandType.P2pPreload, Payload = payload };
        await dispatcher.DispatchAsync(command, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}
