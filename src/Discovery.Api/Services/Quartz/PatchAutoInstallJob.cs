using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Patches;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Instalação automática de patches aprovados dentro da janela de manutenção.
///
/// Regras de segurança:
/// - só roda quando a política do escopo tem AutoInstallEnabled;
/// - respeita a janela (timezone do site) e o limite MaxInstallsPerRun;
/// - nunca reinicia (RebootPolicy/RebootAllowed são aplicados pelo operador);
/// - ignora agentes em manutenção/offline e updates recusados;
/// - lock distribuído por agente no Redis (Quartz é in-memory por instância).
/// </summary>
[DisallowConcurrentExecution]
public sealed class PatchAutoInstallJob : IJob
{
    public static readonly JobKey Key = new("patch-auto-install", "patches");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<PatchAutoInstallJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var patchRepo = scope.ServiceProvider.GetRequiredService<IPatchRepository>();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var siteRepo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();
        var redis = scope.ServiceProvider.GetRequiredService<IRedisService>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IAgentCommandDispatcher>();
        var reportRepo = scope.ServiceProvider.GetRequiredService<IAutomationExecutionReportRepository>();

        try
        {
            var agentIds = await patchRepo.GetAgentIdsInScopeAsync(
                null, null, true, [], 300, ct);
            if (agentIds.Count == 0)
                return;

            var pending = await patchRepo.GetPendingForAgentsAsync(agentIds, ct);
            var approved = pending
                .Where(update => update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved)
                .GroupBy(update => update.AgentId)
                .ToList();

            if (approved.Count == 0)
                return;

            var now = DateTime.UtcNow;
            var dispatched = 0;

            foreach (var group in approved)
            {
                if (ct.IsCancellationRequested)
                    break;

                var agent = await agentRepo.GetByIdAsync(group.Key);
                if (agent is null || agent.MaintenanceEnabled)
                    continue;

                var site = await siteRepo.GetByIdAsync(agent.SiteId);
                if (site is null)
                    continue;

                var policies = await patchRepo.GetApplicablePoliciesForAgentAsync(site.ClientId, site.Id, ct);
                var policy = policies.OrderByDescending(item => (int)item.ScopeType).FirstOrDefault();
                if (policy is null || !policy.AutoInstallEnabled)
                    continue;

                if (!Discovery.Core.Helpers.PatchMaintenanceWindow.IsOpen(policy.MaintenanceWindowJson, now))
                    continue;

                var lockKey = $"patch:auto-install:{agent.Id}";
                if (!await redis.SetIfNotExistsAsync(lockKey, now.ToString("O"), 3600))
                    continue;

                var targets = group
                    .Take(Math.Clamp(policy.MaxInstallsPerRun <= 0 ? 50 : policy.MaxInstallsPerRun, 1, 100))
                    .ToList();
                if (targets.Count == 0)
                    continue;

                var payload = PatchCommandPayloads.Install(targets, force: false, postAction: "none");
                var command = new AgentCommand
                {
                    AgentId = agent.Id,
                    CommandType = CommandType.WindowsUpdate,
                    Payload = payload
                };
                var created = await dispatcher.DispatchAsync(command, ct);
                await Discovery.Infrastructure.Cqrs.Agents.CommandHandlers.RunAutomationTaskCommandHandler.CreateReportAsync(
                    reportRepo, created, null, null, AutomationExecutionSourceType.PatchInstall,
                    new
                    {
                        mode = "auto-install",
                        policyScope = policy.ScopeType.ToString(),
                        updateKeys = targets.Select(update => update.UpdateKey).ToList()
                    });

                await patchRepo.UpdateInstallStateAsync(
                    agent.Id, targets.Select(update => update.UpdateKey).ToList(),
                    PatchUpdateStatus.Installing, null, null, ct);

                dispatched++;
            }

            if (dispatched > 0)
                logger.LogInformation("Patch auto-install: {Count} agente(s) receberam instalação automática.", dispatched);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Patch auto-install falhou.");
        }
    }
}

/// <summary>
/// Manutenção diária do módulo de patches: expira instalações sem resultado
/// (comando perdido/agente reiniciado) e reconcilia aprovações defasadas.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PatchMaintenanceJob : IJob
{
    public static readonly JobKey Key = new("patch-maintenance", "patches");

    public async Task Execute(IJobExecutionContext context)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<PatchMaintenanceJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var patchRepo = scope.ServiceProvider.GetRequiredService<IPatchRepository>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPatchApprovalResolver>();

        try
        {
            var expired = await patchRepo.ExpireStaleInstallingAsync(TimeSpan.FromHours(24), ct);
            if (expired > 0)
                logger.LogInformation("Patch maintenance: {Count} instalação(ões) expirada(s) sem resultado.", expired);

            // Retenção: linhas de atualizações não vistas há mais de 90 dias.
            var purged = await patchRepo.PurgeStaleInventoryAsync(TimeSpan.FromDays(90), ct);
            if (purged > 0)
                logger.LogInformation("Patch maintenance: {Count} linha(s) de inventário antigo removida(s).", purged);

            // Reconcilia aprovações de agentes COM pendências (inclusive offline),
            // com teto de 1000 agentes por execução. O resolver usa o join de frota
            // e ignora agentes deletados.
            var changed = await resolver.RecomputeForScopeAsync(
                Discovery.Core.Enums.PatchApprovalScopeType.Global, null, ct);

            if (changed > 0)
                logger.LogInformation("Patch maintenance: {Count} linha(s) de aprovação reconciliada(s).", changed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Patch maintenance falhou.");
        }
    }
}
