using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Core.Cqrs.Agents.Notifications.Commands;

/// <summary>
/// Envia uma notificação avulsa para o usuário de um agent específico,
/// exibida na sessão interativa da máquina via PSADT (commandType
/// ShowPsadtAlert). Suporta prompt modal que aguarda o clique em OK
/// (waitForUser) ou auto-fechamento após um timeout configurado.
/// </summary>
public sealed record SendAgentNotificationCommand(
    Guid AgentId,
    string Title,
    string Message,
    PsadtAlertType AlertType = PsadtAlertType.Modal,
    int? TimeoutSeconds = null,
    string? Icon = null,
    string? DefaultAction = null
) : ICommand<Result<Guid>>;

public sealed class SendAgentNotificationCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<SendAgentNotificationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SendAgentNotificationCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<Guid>.Failure(Error.NotFound("Agent not found."));

        var title = (cmd.Title ?? string.Empty).Trim();
        var message = (cmd.Message ?? string.Empty).Trim();

        if (title.Length == 0)
            return Result<Guid>.Failure(Error.Validation("title", "o título é obrigatório."));
        if (message.Length == 0)
            return Result<Guid>.Failure(Error.Validation("message", "a mensagem é obrigatória."));
        if (title.Length > PsadtAlertPayloadFactory.MaxTitleLength)
            return Result<Guid>.Failure(Error.Validation("title", $"o título deve ter no máximo {PsadtAlertPayloadFactory.MaxTitleLength} caracteres."));
        if (message.Length > PsadtAlertPayloadFactory.MaxMessageLength)
            return Result<Guid>.Failure(Error.Validation("message", $"a mensagem deve ter no máximo {PsadtAlertPayloadFactory.MaxMessageLength} caracteres."));

        var alertId = Guid.NewGuid();
        var payload = PsadtAlertPayloadFactory.Build(
            alertId,
            title,
            message,
            cmd.AlertType,
            cmd.TimeoutSeconds,
            cmd.Icon,
            cmd.DefaultAction);

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.ShowPsadtAlert,
            Payload = payload
        };

        await dispatcher.DispatchAsync(command, ct);
        return Result<Guid>.Success(alertId);
    }
}
