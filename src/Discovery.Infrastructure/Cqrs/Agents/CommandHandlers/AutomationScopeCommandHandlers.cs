using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Agents.Automation.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

/// <summary>
/// Validação do escopo de uma operação em massa. Mantida pura (sem repositório)
/// para ser testável isoladamente.
/// </summary>
internal static class AutomationScopeValidation
{
    /// <summary>Teto de agentes por lote — evita um request que dispara milhares de comandos.</summary>
    internal const int MaxAgentsPerBatch = 2000;

    internal static Error? ValidateTarget(Guid? clientId, Guid? siteId)
    {
        if (clientId.HasValue == siteId.HasValue)
            return Error.Validation("scope", "Informe exatamente um escopo: clientId ou siteId.");
        return null;
    }

    internal static Error? ValidateSiteOwnership(Site? site, Guid? clientId)
    {
        if (site is null)
            return Error.NotFound("Site not found.");

        // Defesa contra escopo cruzado: a rota valida a permissão no par
        // (clientId, siteId), mas um site de outro cliente poderia ser informado
        // junto de um cliente permitido.
        if (clientId.HasValue && site.ClientId != clientId.Value)
            return Error.Validation("scope", "O site informado não pertence ao cliente informado.");

        return null;
    }

    internal static Error? ValidateBatchSize(int agentCount)
        => agentCount > MaxAgentsPerBatch
            ? Error.Validation("scope", $"O escopo tem {agentCount} agentes e excede o limite de {MaxAgentsPerBatch} por lote.")
            : null;
}

/// <summary>Escopo resolvido: nome ("client"/"site"), id e agentes candidatos.</summary>
internal sealed record AutomationScopeTarget(string Name, Guid Id, IReadOnlyList<Agent> Agents);

internal static class AutomationScopeResolver
{
    internal static async Task<(AutomationScopeTarget? Scope, Error? Error)> ResolveAsync(
        IAgentRepository agentRepo,
        ISiteRepository siteRepo,
        IClientRepository clientRepo,
        Guid? clientId,
        Guid? siteId,
        CancellationToken ct)
    {
        var targetError = AutomationScopeValidation.ValidateTarget(clientId, siteId);
        if (targetError is not null)
            return (null, targetError);

        if (siteId.HasValue)
        {
            var site = await siteRepo.GetByIdAsync(siteId.Value);
            var ownershipError = AutomationScopeValidation.ValidateSiteOwnership(site, clientId);
            if (ownershipError is not null)
                return (null, ownershipError);

            var agents = await agentRepo.GetBySiteIdAsync(siteId.Value);
            return (new AutomationScopeTarget("site", siteId.Value, agents.ToList()), null);
        }

        var client = await clientRepo.GetByIdAsync(clientId!.Value);
        if (client is null)
            return (null, Error.NotFound("Client not found."));

        var clientAgents = await agentRepo.GetByClientIdAsync(clientId.Value);
        return (new AutomationScopeTarget("client", clientId.Value, clientAgents.ToList()), null);
    }
}

internal static class AutomationScopeBatch
{
    internal static string ResolveCorrelationId(string? correlationId, string scopeName)
        => string.IsNullOrWhiteSpace(correlationId)
            ? $"bulk-{scopeName}-{Guid.NewGuid():N}"
            : correlationId.Trim();
}

/// <summary>
/// Loop de disparo de um lote. Um agente que falha não aborta o lote: o erro é
/// registrado no item correspondente para o operador agir pontualmente.
/// </summary>
internal static class AutomationScopeDispatcher
{
    internal static async Task<AutomationScopeDispatchResultDto> DispatchAsync(
        IReadOnlyList<Agent> candidates,
        string scopeName,
        Guid scopeId,
        CommandType commandType,
        string payload,
        string correlationId,
        Guid? taskId,
        Guid? scriptId,
        AutomationExecutionSourceType sourceType,
        object metadata,
        bool queueOfflineAgents,
        IAgentCommandDispatcher dispatcher,
        IAutomationExecutionReportRepository reportRepo,
        CancellationToken ct)
    {
        var agents = candidates
            .Where(agent => agent.DeletedAt is null)
            .OrderBy(agent => agent.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = new List<AutomationScopeDispatchItemDto>(agents.Count);
        var dispatched = 0;
        var queued = 0;
        var failed = 0;
        var skippedOffline = 0;
        var skippedMaintenance = 0;

        foreach (var agent in agents)
        {
            ct.ThrowIfCancellationRequested();

            // Manutenção é exclusão deliberada do alvo da operação.
            if (agent.EffectiveStatus == AgentStatus.Maintenance)
            {
                skippedMaintenance++;
                items.Add(new AutomationScopeDispatchItemDto(
                    agent.Id, agent.Hostname, AutomationScopeDispatchStatus.SkippedMaintenance, null));
                continue;
            }

            var offline = agent.EffectiveStatus != AgentStatus.Online;

            // Sem reentrega ativa, criar comando para agente offline seria criar
            // uma linha órfã que nunca executa — melhor recusar e reportar.
            if (offline && !queueOfflineAgents)
            {
                skippedOffline++;
                items.Add(new AutomationScopeDispatchItemDto(
                    agent.Id, agent.Hostname, AutomationScopeDispatchStatus.SkippedOffline, null));
                continue;
            }

            try
            {
                // Instância nova por agente: o dispatcher/EF atribuem Id e o
                // objeto é rastreado individualmente.
                var command = new AgentCommand
                {
                    AgentId = agent.Id,
                    CommandType = commandType,
                    Payload = payload
                };

                var created = await dispatcher.DispatchAsync(command, ct);
                await RunAutomationTaskCommandHandler.CreateReportAsync(
                    reportRepo, created, taskId, scriptId, sourceType, metadata, correlationId);

                if (offline)
                {
                    queued++;
                    items.Add(new AutomationScopeDispatchItemDto(
                        agent.Id, agent.Hostname, AutomationScopeDispatchStatus.Queued, "agent offline: aguardando reconexão"));
                }
                else
                {
                    dispatched++;
                    items.Add(new AutomationScopeDispatchItemDto(
                        agent.Id, agent.Hostname, AutomationScopeDispatchStatus.Dispatched, null));
                }
            }
            catch (Exception ex)
            {
                failed++;
                items.Add(new AutomationScopeDispatchItemDto(
                    agent.Id, agent.Hostname, AutomationScopeDispatchStatus.Failed, ex.Message));
            }
        }

        return new AutomationScopeDispatchResultDto(
            scopeName,
            scopeId,
            correlationId,
            agents.Count,
            dispatched + queued + failed,
            dispatched,
            queued,
            failed,
            skippedOffline,
            skippedMaintenance,
            items);
    }
}

/// <summary>
/// Pré-condições comuns aos três handlers de escopo (escopo válido, dentro do
/// limite do lote e transporte em tempo real disponível).
/// </summary>
internal static class AutomationScopePreconditions
{
    internal static Error? ValidateTransport(IAgentMessaging messaging)
        => messaging.IsConnected
            ? null
            : Error.Validation("NATS", "Transporte em tempo real indisponível: operações em massa exigem o NATS conectado.");

    internal static Error? ValidateScope(AutomationScopeTarget? scope)
    {
        if (scope is null)
            return Error.NotFound("Scope not found.");

        var sizeError = AutomationScopeValidation.ValidateBatchSize(scope.Agents.Count);
        if (sizeError is not null)
            return sizeError;

        return scope.Agents.Count(agent => agent.DeletedAt is null) == 0
            ? Error.NotFound("Nenhum agente encontrado para o escopo informado.")
            : null;
    }
}

public sealed class RunAutomationTaskForScopeCommandHandler(
    IAutomationTaskService taskService,
    IAutomationScriptService scriptService,
    IAppPackageRepository appPackageRepo,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IClientRepository clientRepo,
    IAgentMessaging messaging,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo,
    IOptionsMonitor<NatsCommandRedeliveryOptions> redeliveryOptions
) : IRequestHandler<RunAutomationTaskForScopeCommand, Result<AutomationScopeDispatchResultDto>>
{
    public async Task<Result<AutomationScopeDispatchResultDto>> Handle(RunAutomationTaskForScopeCommand cmd, CancellationToken ct)
    {
        var (scope, scopeError) = await AutomationScopeResolver.ResolveAsync(
            agentRepo, siteRepo, clientRepo, cmd.ClientId, cmd.SiteId, ct);
        if (scopeError is not null)
            return Result<AutomationScopeDispatchResultDto>.Failure(scopeError);

        var preconditionError = AutomationScopePreconditions.ValidateScope(scope)
            ?? AutomationScopePreconditions.ValidateTransport(messaging);
        if (preconditionError is not null)
            return Result<AutomationScopeDispatchResultDto>.Failure(preconditionError);

        var task = await taskService.GetByIdAsync(cmd.TaskId, includeInactive: false, ct);
        if (task is null)
            return Result<AutomationScopeDispatchResultDto>.Failure(Error.NotFound("Automation task not found or inactive."));

        AgentCommand template;
        try
        {
            // O payload (conteúdo do script, switches silenciosos do winget/choco)
            // é independente do agente: resolve uma vez e reaproveita no lote.
            template = await RunAutomationTaskCommandHandler.BuildAgentCommandFromTaskAsync(
                Guid.Empty, task, scriptService, appPackageRepo, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Result<AutomationScopeDispatchResultDto>.Failure(Error.Validation("task", ex.Message));
        }

        var result = await AutomationScopeDispatcher.DispatchAsync(
            scope!.Agents,
            scope.Name,
            scope.Id,
            template.CommandType,
            template.Payload,
            AutomationScopeBatch.ResolveCorrelationId(cmd.CorrelationId, scope.Name),
            task.Id,
            task.ScriptId,
            AutomationExecutionSourceType.RunNow,
            new { mode = "task-run-now", batch = true, scope = scope.Name, scopeId = scope.Id, actionType = task.ActionType.ToString() },
            redeliveryOptions.CurrentValue.Enabled,
            dispatcher,
            reportRepo,
            ct);

        return Result<AutomationScopeDispatchResultDto>.Success(result);
    }
}

public sealed class RunAutomationScriptForScopeCommandHandler(
    IAutomationScriptService scriptService,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IClientRepository clientRepo,
    IAgentMessaging messaging,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo,
    IOptionsMonitor<NatsCommandRedeliveryOptions> redeliveryOptions
) : IRequestHandler<RunAutomationScriptForScopeCommand, Result<AutomationScopeDispatchResultDto>>
{
    public async Task<Result<AutomationScopeDispatchResultDto>> Handle(RunAutomationScriptForScopeCommand cmd, CancellationToken ct)
    {
        var (scope, scopeError) = await AutomationScopeResolver.ResolveAsync(
            agentRepo, siteRepo, clientRepo, cmd.ClientId, cmd.SiteId, ct);
        if (scopeError is not null)
            return Result<AutomationScopeDispatchResultDto>.Failure(scopeError);

        var preconditionError = AutomationScopePreconditions.ValidateScope(scope)
            ?? AutomationScopePreconditions.ValidateTransport(messaging);
        if (preconditionError is not null)
            return Result<AutomationScopeDispatchResultDto>.Failure(preconditionError);

        var script = await scriptService.GetByIdAsync(cmd.ScriptId, includeInactive: false, ct);
        if (script is null)
            return Result<AutomationScopeDispatchResultDto>.Failure(Error.NotFound("Automation script not found or inactive."));

        var result = await AutomationScopeDispatcher.DispatchAsync(
            scope!.Agents,
            scope.Name,
            scope.Id,
            CommandType.Script,
            script.Content,
            AutomationScopeBatch.ResolveCorrelationId(cmd.CorrelationId, scope.Name),
            null,
            script.Id,
            AutomationExecutionSourceType.RunNow,
            new { mode = "script-run-now", batch = true, scope = scope.Name, scopeId = scope.Id, version = script.Version, contentHash = script.ContentHashSha256 },
            redeliveryOptions.CurrentValue.Enabled,
            dispatcher,
            reportRepo,
            ct);

        return Result<AutomationScopeDispatchResultDto>.Success(result);
    }
}

public sealed class ForceAutomationSyncForScopeCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IClientRepository clientRepo,
    IAgentMessaging messaging,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo,
    IOptionsMonitor<NatsCommandRedeliveryOptions> redeliveryOptions
) : IRequestHandler<ForceAutomationSyncForScopeCommand, Result<AutomationScopeDispatchResultDto>>
{
    public async Task<Result<AutomationScopeDispatchResultDto>> Handle(ForceAutomationSyncForScopeCommand cmd, CancellationToken ct)
    {
        var (scope, scopeError) = await AutomationScopeResolver.ResolveAsync(
            agentRepo, siteRepo, clientRepo, cmd.ClientId, cmd.SiteId, ct);
        if (scopeError is not null)
            return Result<AutomationScopeDispatchResultDto>.Failure(scopeError);

        var preconditionError = AutomationScopePreconditions.ValidateScope(scope)
            ?? AutomationScopePreconditions.ValidateTransport(messaging);
        if (preconditionError is not null)
            return Result<AutomationScopeDispatchResultDto>.Failure(preconditionError);

        // Mesma regra do force sync por agente: sem flags informadas, default
        // histórico (policies + inventory); com flags, o valor explícito manda.
        var flags = ForceAutomationSyncCommandHandler.ResolveForceSyncFlags(
            new ForceAutomationSyncCommand(Guid.Empty, null, cmd.Policies, cmd.Inventory, cmd.Software, cmd.AppStore));

        var payload = JsonSerializer.Serialize(new
        {
            Operation = "force-sync",
            Policies = flags.Policies,
            Inventory = flags.Inventory,
            Software = flags.Software,
            AppStore = flags.AppStore,
            TaskIds = (string?)null,
            RequestedAt = DateTime.UtcNow
        });

        var result = await AutomationScopeDispatcher.DispatchAsync(
            scope!.Agents,
            scope.Name,
            scope.Id,
            CommandType.SystemInfo,
            payload,
            AutomationScopeBatch.ResolveCorrelationId(cmd.CorrelationId, scope.Name),
            null,
            null,
            AutomationExecutionSourceType.ForceSync,
            new { mode = "force-sync", batch = true, scope = scope.Name, scopeId = scope.Id, flags.Policies, flags.Inventory, flags.Software, flags.AppStore },
            redeliveryOptions.CurrentValue.Enabled,
            dispatcher,
            reportRepo,
            ct);

        return Result<AutomationScopeDispatchResultDto>.Success(result);
    }
}
