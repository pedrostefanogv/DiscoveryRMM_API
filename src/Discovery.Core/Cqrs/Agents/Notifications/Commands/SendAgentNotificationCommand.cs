using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
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
    private const int MaxTitleLength = 120;
    private const int MaxMessageLength = 2000;

    // Show-ADTDialogBox rejeita -Timeout maior que UI.DefaultTimeout do
    // config.psd1 do PSADT (padrão 3300s) e, nesse caso, nenhum diálogo é
    // exibido. O agent ainda faz clamp/retry, mas limitar aqui mantém o
    // contrato da API coerente com o que o endpoint realmente consegue exibir.
    private const int MaxTimeoutSeconds = 3300;

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

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
        if (title.Length > MaxTitleLength)
            return Result<Guid>.Failure(Error.Validation("title", $"o título deve ter no máximo {MaxTitleLength} caracteres."));
        if (message.Length > MaxMessageLength)
            return Result<Guid>.Failure(Error.Validation("message", $"a mensagem deve ter no máximo {MaxMessageLength} caracteres."));

        // UpdateProgress é reservado ao self-update; avisos avulsos são modal/toast.
        var type = cmd.AlertType == PsadtAlertType.Toast ? "toast" : "modal";

        var requestedTimeout = cmd.TimeoutSeconds.GetValueOrDefault();
        int? timeoutSeconds;
        bool waitForUser;

        if (type == "toast")
        {
            // Toast sempre auto-fecha; default curto quando não informado.
            timeoutSeconds = requestedTimeout > 0 ? Math.Min(requestedTimeout, MaxTimeoutSeconds) : 15;
            waitForUser = false;
        }
        else if (requestedTimeout > 0)
        {
            timeoutSeconds = Math.Min(requestedTimeout, MaxTimeoutSeconds);
            waitForUser = false;
        }
        else
        {
            // Modal sem timeout informado: permanece aberto até o usuário clicar em OK.
            timeoutSeconds = null;
            waitForUser = true;
        }

        var alertId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            alertId = alertId.ToString(),
            type,
            title,
            message,
            timeoutSeconds,
            waitForUser,
            icon = NormalizeIcon(cmd.Icon),
            defaultAction = string.IsNullOrWhiteSpace(cmd.DefaultAction) ? null : cmd.DefaultAction.Trim()
        }, PayloadJsonOptions);

        var command = new AgentCommand
        {
            AgentId = cmd.AgentId,
            CommandType = CommandType.ShowPsadtAlert,
            Payload = payload
        };

        await dispatcher.DispatchAsync(command, ct);
        return Result<Guid>.Success(alertId);
    }

    private static string NormalizeIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
            return "info";

        return icon.Trim().ToLowerInvariant() switch
        {
            "warning" or "warn" => "warning",
            "error" => "error",
            "question" => "question",
            _ => "info"
        };
    }
}
