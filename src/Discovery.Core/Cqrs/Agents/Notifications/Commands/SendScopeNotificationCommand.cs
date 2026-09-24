using Discovery.Core.Cqrs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Core.Cqrs.Agents.Notifications.Commands;

/// <summary>
/// Resultado de um broadcast de notificação para um escopo
/// (cliente, site, agent ou label).
/// </summary>
public sealed record SendScopeNotificationResult(
    Guid AlertId,
    AlertScopeType ScopeType,
    int TotalAgents,
    int Dispatched,
    int Failed);

/// <summary>
/// Envia a mesma notificação PSADT (modal/toast) para todos os agents de um
/// escopo: um cliente inteiro, um site, uma label ou um único agent.
/// Diferente de <see cref="SendAgentNotificationCommand"/>, não exige o
/// carregamento individual de cada agent pelo chamador — a API resolve o
/// escopo no servidor e devolve a contagem de entregas.
/// </summary>
public sealed record SendScopeNotificationCommand(
    AlertScopeType ScopeType,
    string Title,
    string Message,
    Guid? ScopeClientId = null,
    Guid? ScopeSiteId = null,
    Guid? ScopeAgentId = null,
    string? ScopeLabelName = null,
    PsadtAlertType AlertType = PsadtAlertType.Modal,
    int? TimeoutSeconds = null,
    string? Icon = null
) : ICommand<Result<SendScopeNotificationResult>>;

public sealed class SendScopeNotificationCommandHandler(
    IAgentRepository agentRepo,
    IAgentLabelRepository labelRepo,
    IAgentCommandDispatcher dispatcher
) : IRequestHandler<SendScopeNotificationCommand, Result<SendScopeNotificationResult>>
{
    private const int LabelPageSize = 1000;

    public async Task<Result<SendScopeNotificationResult>> Handle(
        SendScopeNotificationCommand cmd,
        CancellationToken ct)
    {
        var title = (cmd.Title ?? string.Empty).Trim();
        var message = (cmd.Message ?? string.Empty).Trim();

        if (title.Length == 0)
            return Result<SendScopeNotificationResult>.Failure(Error.Validation("title", "o título é obrigatório."));
        if (message.Length == 0)
            return Result<SendScopeNotificationResult>.Failure(Error.Validation("message", "a mensagem é obrigatória."));
        if (title.Length > PsadtAlertPayloadFactory.MaxTitleLength)
            return Result<SendScopeNotificationResult>.Failure(Error.Validation("title", $"o título deve ter no máximo {PsadtAlertPayloadFactory.MaxTitleLength} caracteres."));
        if (message.Length > PsadtAlertPayloadFactory.MaxMessageLength)
            return Result<SendScopeNotificationResult>.Failure(Error.Validation("message", $"a mensagem deve ter no máximo {PsadtAlertPayloadFactory.MaxMessageLength} caracteres."));

        var resolved = await ResolveAgentIdsAsync(cmd, ct);
        if (resolved.Error is not null)
            return Result<SendScopeNotificationResult>.Failure(resolved.Error);

        var agentIds = resolved.AgentIds;

        // Reutiliza o mesmo alertId para todo o broadcast: permite correlação
        // no relatório de comandos e deduplicação no lado do agent.
        var alertId = Guid.NewGuid();

        if (agentIds.Count == 0)
            return Result<SendScopeNotificationResult>.Success(
                new SendScopeNotificationResult(alertId, cmd.ScopeType, 0, 0, 0));

        var payload = PsadtAlertPayloadFactory.Build(
            alertId,
            title,
            message,
            cmd.AlertType,
            cmd.TimeoutSeconds,
            cmd.Icon);

        var dispatched = 0;
        var failed = 0;

        foreach (var agentId in agentIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var command = new AgentCommand
                {
                    AgentId = agentId,
                    CommandType = CommandType.ShowPsadtAlert,
                    Payload = payload
                };

                await dispatcher.DispatchAsync(command, ct);
                dispatched++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Um agent fora do ar não pode abortar o broadcast inteiro:
                // contabiliza a falha e segue para os demais.
                failed++;
            }
        }

        return Result<SendScopeNotificationResult>.Success(
            new SendScopeNotificationResult(alertId, cmd.ScopeType, agentIds.Count, dispatched, failed));
    }

    private async Task<(IReadOnlyList<Guid> AgentIds, Error? Error)> ResolveAgentIdsAsync(
        SendScopeNotificationCommand cmd,
        CancellationToken ct)
    {
        switch (cmd.ScopeType)
        {
            case AlertScopeType.Agent:
                if (!cmd.ScopeAgentId.HasValue || cmd.ScopeAgentId.Value == Guid.Empty)
                    return ([], Error.Validation("scopeAgentId", "um agent válido é obrigatório para o escopo Agent."));

                var agent = await agentRepo.GetByIdAsync(cmd.ScopeAgentId.Value);
                return agent is null
                    ? ([], Error.NotFound("Agent not found."))
                    : ([agent.Id], null);

            case AlertScopeType.Site:
                if (!cmd.ScopeSiteId.HasValue || cmd.ScopeSiteId.Value == Guid.Empty)
                    return ([], Error.Validation("scopeSiteId", "um site válido é obrigatório para o escopo Site."));

                var siteAgents = await agentRepo.GetBySiteIdAsync(cmd.ScopeSiteId.Value);
                return (siteAgents.Select(a => a.Id).Distinct().ToList(), null);

            case AlertScopeType.Client:
                if (!cmd.ScopeClientId.HasValue || cmd.ScopeClientId.Value == Guid.Empty)
                    return ([], Error.Validation("scopeClientId", "um cliente válido é obrigatório para o escopo Client."));

                var clientAgents = await agentRepo.GetByClientIdAsync(cmd.ScopeClientId.Value);
                return (clientAgents.Select(a => a.Id).Distinct().ToList(), null);

            case AlertScopeType.Label:
                var labelName = (cmd.ScopeLabelName ?? string.Empty).Trim();
                if (labelName.Length == 0)
                    return ([], Error.Validation("scopeLabelName", "o nome da label é obrigatório para o escopo Label."));

                return (await ResolveAgentIdsByLabelAsync(labelName, ct), null);

            default:
                return ([], Error.Validation("scopeType", "escopo de notificação inválido."));
        }
    }

    /// <summary>
    /// Resolve agentes por label usando paginação por cursor — evita carregar
    /// as labels de toda a frota em memória.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveAgentIdsByLabelAsync(string label, CancellationToken ct)
    {
        var ids = new List<Guid>();
        Guid? after = null;

        while (true)
        {
            var page = await labelRepo.GetAgentIdsByLabelPagedAsync(label, after, LabelPageSize, ct);
            if (page.Count == 0)
                break;

            ids.AddRange(page);
            after = page[^1];

            if (page.Count < LabelPageSize)
                break;
        }

        return ids.Distinct().ToList();
    }
}
