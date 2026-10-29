using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
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

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var scopeFactory = context.GetScopedService<IServiceScopeFactory>();
        var logger = context.GetLogger<PatchAutoInstallJob>();
        var ct = context.CancellationToken;

        await using var scope = scopeFactory.CreateAsyncScope();
        var patchRepo = scope.ServiceProvider.GetRequiredService<IPatchRepository>();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        var siteRepo = scope.ServiceProvider.GetRequiredService<ISiteRepository>();
        var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
        var redis = scope.ServiceProvider.GetRequiredService<IRedisService>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IAgentCommandDispatcher>();
        var reportRepo = scope.ServiceProvider.GetRequiredService<IAutomationExecutionReportRepository>();
        var catalogRepo = scope.ServiceProvider.GetRequiredService<IAppCatalogRepository>();

        try
        {
            var agentIds = await patchRepo.GetAgentIdsInScopeAsync(
                null, null, true, [], 300, ct);
            if (agentIds.Count == 0)
                return;

            var nowUtc = DateTime.UtcNow;
            var pending = await patchRepo.GetPendingForAgentsAsync(agentIds, ct);
            var approved = pending
                .Where(update => update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved)
                // Upgrade WaaS sintetico e informativo: nao ha pacote WUA para
                // o job automatico instalar (mesmo se a politica auto-aprovar).
                .Where(update => !PatchSourcePolicy.IsWaaSFeatureSource(update.Source))
                // WU-08: linha que já falhou só volta quando o backoff venceu e a
                // classe permite retry (transient, até MaxTransientAttempts).
                // Antes, toda linha Failed era reprocessada a cada execução.
                .Where(update => update.FailureClass is null ||
                                 PatchFailureClassifier.IsRetryDue(
                                     update.FailureClass, update.AttemptCount, update.NextAttemptAt, nowUtc))
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

                // Timezone: o JSON da política manda; sem ele, usa o timezone do
                // site (SiteConfiguration.Timezone) antes de cair para UTC.
                var siteConfig = await configService.GetSiteConfigAsync(site.Id);
                var recurringWindowOpen = PatchMaintenanceWindow.IsOpen(
                    policy.MaintenanceWindowJson, nowUtc, siteConfig?.Timezone);

                // WU-05/D03: deadline vencido AMPLIA a janela até o prazo. Usa a
                // publicação mais antiga do lote: se qualquer alvo venceu, o lote
                // pode ser instalado fora da janela recorrente — com log auditado.
                DateTime? oldestPublished = null;
                foreach (var update in group)
                {
                    if (!update.PublishedAt.HasValue)
                        continue;
                    if (oldestPublished is null || update.PublishedAt.Value < oldestPublished.Value)
                        oldestPublished = update.PublishedAt.Value;
                }

                var window = PatchDeadline.EvaluateWindow(
                    recurringWindowOpen, policy.DeadlineDays, oldestPublished, nowUtc);
                if (!window.Open)
                    continue;
                if (!recurringWindowOpen)
                {
                    logger.LogWarning(
                        "Patch auto-install: janela ampliada por deadline (agent={AgentId} policy={Scope} reason={Reason})",
                        agent.Id, policy.ScopeType, window.Reason);
                }

                var lockKey = $"patch:auto-install:{agent.Id}";
                if (!await redis.SetIfNotExistsAsync(lockKey, now.ToString("O"), 3600))
                    continue;

                var targets = group
                    .Take(Math.Clamp(policy.MaxInstallsPerRun <= 0 ? 50 : policy.MaxInstallsPerRun, 1, 100))
                    .ToList();
                if (targets.Count == 0)
                    continue;

                // WU-19/WU-20: OS e terceiros são comandos distintos (o agente
                // tem executores diferentes); ambos contam como a mesma execução.
                var (osTargets, thirdPartyTargets) = PatchCommandPayloads.SplitBySource(targets);
                var dispatchedForAgent = 0;

                if (osTargets.Count > 0)
                {
                    var osCommand = new AgentCommand
                    {
                        AgentId = agent.Id,
                        CommandType = CommandType.WindowsUpdate,
                        Payload = PatchCommandPayloads.Install(osTargets, force: false, postAction: "none")
                    };
                    var created = await dispatcher.DispatchAsync(osCommand, ct);
                    await Discovery.Infrastructure.Cqrs.Agents.CommandHandlers.RunAutomationTaskCommandHandler.CreateReportAsync(
                        reportRepo, created, null, null, AutomationExecutionSourceType.PatchInstall,
                        new
                        {
                            mode = "auto-install",
                            sources = new[] { "os" },
                            policyScope = policy.ScopeType.ToString(),
                            updateKeys = osTargets.Select(update => update.UpdateKey).ToList()
                        });
                    dispatchedForAgent++;
                }

                if (thirdPartyTargets.Count > 0)
                {
                    // Só chega aqui se a política tem duplo consentimento: o
                    // resolver não auto-aprova terceiros sem ele.
                    foreach (var groupBySource in thirdPartyTargets.GroupBy(
                                 update => (update.Source ?? string.Empty).ToLowerInvariant()))
                    {
                        // WU-21: leva metadados do catálogo curado (URL+hash) para
                        // o agente baixar do fornecedor e validar antes de executar.
                        var appIds = groupBySource
                            .Select(update => PatchCommandPayloads.ExtractAppId(update.UpdateKey))
                            .ToList();
                        var catalogEntries = await catalogRepo.GetActiveEntriesForAsync(
                            groupBySource.Key, appIds, ct);
                        var catalogMap = PatchCommandPayloads.BuildCatalogMap(catalogEntries);

                        var tpCommand = new AgentCommand
                        {
                            AgentId = agent.Id,
                            CommandType = CommandType.WindowsUpdate,
                            Payload = PatchCommandPayloads.InstallThirdParty(
                                groupBySource.Key, groupBySource.ToList(), 1800, catalogMap)
                        };
                        var created = await dispatcher.DispatchAsync(tpCommand, ct);
                        await Discovery.Infrastructure.Cqrs.Agents.CommandHandlers.RunAutomationTaskCommandHandler.CreateReportAsync(
                            reportRepo, created, null, null, AutomationExecutionSourceType.PatchInstall,
                            new
                            {
                                mode = "auto-install",
                                sources = new[] { groupBySource.Key },
                                policyScope = policy.ScopeType.ToString(),
                                updateKeys = groupBySource.Select(update => update.UpdateKey).ToList()
                            });
                        dispatchedForAgent++;
                    }
                }

                await patchRepo.UpdateInstallStateAsync(
                    agent.Id, targets.Select(update => update.UpdateKey).ToList(),
                    PatchUpdateStatus.Installing, null, null, ct);

                dispatched += dispatchedForAgent;
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

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
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
