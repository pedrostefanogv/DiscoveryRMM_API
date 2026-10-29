using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Discovery.Api.Services.Quartz;

/// <summary>
/// Consome a POLÍTICA de reboot para agentes com reinício pendente (decisão D02,
/// 2026-10-29).
///
/// Contexto: `PatchPolicy.RebootPolicy` e `RebootAllowed` existiam, eram
/// editáveis na UI e não eram lidos por nenhum consumidor — configuração morta.
/// Como o produto decidiu NÃO reiniciar máquinas automaticamente, a política
/// vira sinal operacional: este job calcula a ação (notificar / notificar com
/// dica de janela) e persiste no snapshot, onde a aba "Atualizações" e o suporte
/// conseguem ver por que o reinício está pendente e o que a política manda fazer.
///
/// O job nunca reinicia nada: reiniciar continua sendo ação explícita do
/// operador (`postAction` do comando de instalação).
/// </summary>
[DisallowConcurrentExecution]
public sealed class PatchRebootNoticeJob : IJob
{
    /// <summary>Teto de agentes por execução: o job roda a cada poucos minutos.</summary>
    private const int MaxAgentsPerRun = 500;

    public static readonly JobKey Key = new("patch-reboot-notice", "patches");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<PatchRebootNoticeJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var patchRepo = scope.ServiceProvider.GetRequiredService<IPatchRepository>();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var siteRepo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();
        var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();

        try
        {
            var agentIds = await patchRepo.GetAgentsWithPendingRebootAsync(MaxAgentsPerRun, ct);
            if (agentIds.Count == 0)
                return;

            var now = DateTime.UtcNow;
            var decided = 0;

            foreach (var agentId in agentIds)
            {
                if (ct.IsCancellationRequested)
                    break;

                var agent = await agentRepo.GetByIdAsync(agentId);
                if (agent is null || agent.MaintenanceEnabled)
                    continue;

                var site = await siteRepo.GetByIdAsync(agent.SiteId);
                if (site is null)
                    continue;

                var policies = await patchRepo.GetApplicablePoliciesForAgentAsync(site.ClientId, site.Id, ct);
                // Política mais específica vence (Site > Client > Global).
                var policy = policies.OrderByDescending(item => (int)item.ScopeType).FirstOrDefault();
                if (policy is null)
                    continue;

                var siteConfig = await configService.GetSiteConfigAsync(site.Id);
                var inWindow = PatchMaintenanceWindow.IsOpen(
                    policy.MaintenanceWindowJson, now, siteConfig?.Timezone);

                var (action, reason) = PatchRebootDecision.Evaluate(
                    policy.RebootPolicy, policy.RebootAllowed, rebootRequired: true, inWindow);

                var snapshot = await patchRepo.GetSnapshotAsync(agentId, ct);
                if (snapshot is not null &&
                    snapshot.RebootAction == action &&
                    string.Equals(snapshot.RebootActionReason, reason, StringComparison.Ordinal))
                {
                    continue;
                }

                await patchRepo.UpdateRebootActionAsync(agentId, action, reason, now, ct);
                decided++;

                if (action != PatchRebootAction.None)
                {
                    logger.LogWarning(
                        "Patch reboot pendente: agent={AgentId} policy={Scope} action={Action} reason={Reason}",
                        agentId, policy.ScopeType, action, reason);
                }
            }

            if (decided > 0)
                logger.LogInformation("Patch reboot notice: {Count} decisão(ões) de política atualizada(s).", decided);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Patch reboot notice falhou.");
        }
    }
}
