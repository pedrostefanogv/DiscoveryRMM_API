using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Cqrs.Patches;

// ── Agente ────────────────────────────────────────────────────────────────

/// <summary>
/// Botão "Verificar atualizações" da aba do agente: enfileira o comando
/// <c>windowsupdate</c> (action=scan) e devolve SEMPRE uma mensagem explicável
/// para a UI — inclusive quando o agente está offline (o comando é enfileirado
/// e entregue no próximo heartbeat, mas o usuário precisa saber disso).
/// </summary>
public sealed class ScanAgentWindowsUpdatesCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo,
    ILogger<ScanAgentWindowsUpdatesCommandHandler>? logger = null
) : IRequestHandler<ScanAgentWindowsUpdatesCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(ScanAgentWindowsUpdatesCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Agent not found."));

        var online = agent.DeletedAt is null && agent.EffectiveStatus == AgentStatus.Online;
        if (!online)
        {
            logger?.LogInformation(
                "Scan de Windows Update pedido para agente OFFLINE {AgentId} ({Hostname}) por {User}; comando enfileirado.",
                agent.Id, agent.Hostname, cmd.CorrelationId);
        }

        var created = await PatchOperations.DispatchAsync(
            dispatcher, reportRepo, cmd.AgentId,
            PatchCommandPayloads.Scan("on-demand"),
            AutomationExecutionSourceType.PatchScan,
            new { mode = "scan", reason = "on-demand", online },
            cmd.CorrelationId, ct);

        var message = online
            ? "Varredura de atualizações enviada ao agente. O resultado aparece aqui em alguns segundos " +
              "(a varredura completa de Windows Update leva ~10-20s)."
            : "Agente offline: a varredura foi enfileirada e roda no próximo contato do agente. " +
              "Os dados exibidos continuam os do último scan. Se o Windows Update estiver desabilitado " +
              "(serviço wuauserv) ou gerenciado por WSUS, o scan pode não retornar atualizações.";

        logger?.LogInformation(
            "Scan de Windows Update enfileirado: agent={AgentId} command={CommandId} online={Online} correlation={Correlation}",
            agent.Id, created.Id, online, cmd.CorrelationId);

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            1, 0, [created.Id], message));
    }
}

public sealed class InstallAgentWindowsUpdateCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchRepository patchRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<InstallAgentWindowsUpdateCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(InstallAgentWindowsUpdateCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<Guid>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<Guid>.Failure(Error.NotFound("Site not found."));

        var update = await patchRepo.GetAgentUpdateAsync(cmd.AgentId, cmd.UpdateKey, ct);
        if (update is null)
            return Result<Guid>.Failure(Error.NotFound("Windows update not found for this agent."));

        if (!update.IsPending)
            return Result<Guid>.Failure(Error.Validation("updateKey", "A atualização não está pendente neste agente."));

        if (update.ApprovalState == PatchApprovalState.Denied && !cmd.Force)
        {
            return Result<Guid>.Failure(Error.Conflict(
                "Esta atualização está recusada para este agente. Confirme para instalar mesmo assim."));
        }

        if (cmd.Force)
        {
            // Override auditado: quem forçou, quando e por quê.
            await patchRepo.AddApprovalAuditAsync(new PatchApprovalAudit
            {
                Id = Guid.NewGuid(),
                ChangeType = PatchApprovalAuditChangeType.Updated,
                ScopeType = PatchApprovalScopeType.Agent,
                ScopeId = agent.Id,
                ClientId = site.ClientId,
                SiteId = site.Id,
                AgentId = agent.Id,
                UpdateKey = update.GroupKey,
                KbId = update.GroupKey.StartsWith("kb:", StringComparison.OrdinalIgnoreCase)
                    ? update.GroupKey[3..]
                    : null,
                OldAction = update.ApprovalState == PatchApprovalState.Denied
                    ? PatchApprovalActionType.Deny
                    : null,
                NewAction = null,
                Reason = string.IsNullOrWhiteSpace(cmd.Reason)
                    ? "instalação forçada (override de aprovação)"
                    : cmd.Reason.Trim(),
                ChangedBy = cmd.RequestedBy,
                IpAddress = cmd.IpAddress,
                ChangedAt = DateTime.UtcNow
            }, ct);
        }

        var payload = PatchCommandPayloads.Install([update], cmd.Force, cmd.PostAction);
        var created = await PatchOperations.DispatchAsync(
            dispatcher, reportRepo, cmd.AgentId, payload,
            AutomationExecutionSourceType.PatchInstall,
            new
            {
                updateKey = update.UpdateKey,
                groupKey = update.GroupKey,
                update.Title,
                approved = update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved,
                forced = cmd.Force,
                postAction = PatchCommandPayloads.NormalizePostAction(cmd.PostAction),
                requestedBy = cmd.RequestedBy,
                reason = cmd.Reason
            },
            cmd.CorrelationId, ct);

        await patchRepo.UpdateInstallStateAsync(
            cmd.AgentId, [update.UpdateKey], PatchUpdateStatus.Installing, null, null, ct);

        return Result<Guid>.Success(created.Id);
    }
}

public sealed class InstallAgentApprovedWindowsUpdatesCommandHandler(
    IAgentRepository agentRepo,
    IPatchRepository patchRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<InstallAgentApprovedWindowsUpdatesCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(
        InstallAgentApprovedWindowsUpdatesCommand cmd,
        CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Agent not found."));

        var postAction = PatchCommandPayloads.NormalizePostAction(cmd.PostAction);
        if (postAction != "none" && !cmd.Confirm)
        {
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "Confirme a ação de energia (reiniciar/desligar) após a instalação."));
        }

        var pending = await patchRepo.GetPendingForAgentAsync(cmd.AgentId, ct);
        var approved = pending
            .Where(update => update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved)
            .Take(100)
            .ToList();

        if (approved.Count == 0)
        {
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "Nenhuma atualização aprovada para instalar neste agente. Aprove ao menos uma atualização pendente."));
        }

        var payload = PatchCommandPayloads.Install(approved, force: false, postAction);
        var created = await PatchOperations.DispatchAsync(
            dispatcher, reportRepo, cmd.AgentId, payload,
            AutomationExecutionSourceType.PatchInstall,
            new
            {
                mode = "install-approved",
                updateKeys = approved.Select(update => update.UpdateKey).ToList(),
                count = approved.Count,
                postAction
            },
            cmd.CorrelationId, ct);

        await patchRepo.UpdateInstallStateAsync(
            cmd.AgentId, approved.Select(update => update.UpdateKey).ToList(),
            PatchUpdateStatus.Installing, null, null, ct);

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            1, pending.Count - approved.Count, [created.Id],
            postAction == "none"
                ? $"{approved.Count} atualização(ões) aprovada(s) enviada(s) para instalação."
                : $"{approved.Count} atualização(ões) aprovada(s) enviada(s); {postAction} após a instalação."));
    }
}

public sealed class SetAgentWindowsUpdateApprovalCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver,
    IPatchEventPublisher events
) : IRequestHandler<SetAgentWindowsUpdateApprovalCommand, Result<PatchApprovalDto>>
{
    public async Task<Result<PatchApprovalDto>> Handle(
        SetAgentWindowsUpdateApprovalCommand cmd,
        CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<PatchApprovalDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<PatchApprovalDto>.Failure(Error.NotFound("Site not found."));

        var update = await patchRepo.GetAgentUpdateAsync(cmd.AgentId, cmd.UpdateKey, ct);
        if (update is null)
            return Result<PatchApprovalDto>.Failure(Error.NotFound("Windows update not found for this agent."));

        var dto = await PatchOperations.SetApprovalAsync(
            patchRepo, resolver, agent, site, update, cmd.Scope, cmd.Action,
            cmd.Reason, cmd.ChangedBy, cmd.IpAddress, cmd.TargetScopeId, ct);

        await events.PublishApprovalChangedAsync(
            agent.Id, agent.Hostname, site.ClientId, site.Id, update.GroupKey,
            cmd.Action == PatchApprovalActionType.Deny ? "Deny" : "Approve", ct);

        return Result<PatchApprovalDto>.Success(dto);
    }
}

/// <summary>
/// Aprova/recusa TODAS as atualizações pendentes do agente de uma vez (ação em
/// lote da visualização de pendências). A regra é criada no escopo escolhido
/// (default: somente este agente).
/// </summary>
public sealed class ApproveAgentPendingWindowsUpdatesCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver,
    IPatchEventPublisher events
) : IRequestHandler<ApproveAgentPendingWindowsUpdatesCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(
        ApproveAgentPendingWindowsUpdatesCommand cmd,
        CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Site not found."));

        var pending = await patchRepo.GetPendingForAgentAsync(cmd.AgentId, ct);
        var targets = pending
            .GroupBy(update => update.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(200)
            .ToList();

        if (targets.Count == 0)
            return Result<PatchDispatchDto>.Failure(Error.Conflict("Nenhuma atualização pendente para este agente."));

        var changed = 0;
        foreach (var update in targets)
        {
            await PatchOperations.SetApprovalAsync(
                patchRepo, resolver, agent, site, update, cmd.Scope, cmd.Action,
                cmd.Reason, cmd.ChangedBy, cmd.IpAddress, null, ct);
            changed++;
        }

        await events.PublishApprovalChangedAsync(
            agent.Id, agent.Hostname, site.ClientId, site.Id, "bulk-pending",
            cmd.Action == PatchApprovalActionType.Deny ? "Deny" : "Approve", ct);

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            changed, pending.Count - targets.Count, [],
            cmd.Action == PatchApprovalActionType.Deny
                ? $"{changed} atualização(ões) recusada(s)."
                : $"{changed} atualização(ões) aprovada(s)."));
    }
}

public sealed class BulkAgentWindowsUpdateCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver,
    IAppCatalogRepository catalogRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo,
    IPatchEventPublisher events
) : IRequestHandler<BulkAgentWindowsUpdateCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(
        BulkAgentWindowsUpdateCommand cmd,
        CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Site not found."));

        if (cmd.UpdateKeys.Count == 0)
            return Result<PatchDispatchDto>.Failure(Error.Validation("updateKeys", "Informe ao menos uma atualização."));

        var action = cmd.Action?.Trim().ToLowerInvariant();
        if (action is "approve" or "deny")
        {
            var approvalAction = action == "deny" ? PatchApprovalActionType.Deny : PatchApprovalActionType.Approve;
            var scope = cmd.Scope ?? PatchApprovalScopeType.Agent;
            var changed = 0;
            var keys = new List<Guid>();

            foreach (var updateKey in cmd.UpdateKeys.Distinct(StringComparer.OrdinalIgnoreCase).Take(200))
            {
                var update = await patchRepo.GetAgentUpdateAsync(cmd.AgentId, updateKey, ct);
                if (update is null)
                    continue;

                await PatchOperations.SetApprovalAsync(
                    patchRepo, resolver, agent, site, update, scope, approvalAction,
                    cmd.Reason, cmd.ChangedBy, cmd.IpAddress, null, ct);
                changed++;
            }

            await events.PublishApprovalChangedAsync(
                agent.Id, agent.Hostname, site.ClientId, site.Id, "bulk",
                action == "deny" ? "Deny" : "Approve", ct);

            return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
                changed, cmd.UpdateKeys.Count - changed, keys,
                $"{changed} atualização(ões) {(action == "deny" ? "recusada(s)" : "aprovada(s)")}."));
        }

        if (action == "install")
        {
            var pending = await patchRepo.GetPendingForAgentAsync(cmd.AgentId, ct);
            var keysSet = cmd.UpdateKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = pending
                .Where(update => keysSet.Contains(update.UpdateKey) || keysSet.Contains(update.GroupKey))
                .Where(update => update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved)
                .Take(100)
                .ToList();

            if (targets.Count == 0)
            {
                return Result<PatchDispatchDto>.Failure(Error.Conflict(
                    "Nenhuma das atualizações selecionadas está aprovada para instalação."));
            }

            // WU-19/WU-20: OS e terceiros em comandos separados. Terceiros só
            // executam com duplo consentimento na política aplicável (D22).
            var (osTargets, thirdPartyTargets) = PatchCommandPayloads.SplitBySource(targets);
            var bulkPolicies = await patchRepo.GetApplicablePoliciesForAgentAsync(site.ClientId, site.Id, ct);
            var bulkPolicy = bulkPolicies.OrderByDescending(item => (int)item.ScopeType).FirstOrDefault();
            var bulkThirdPartyConsented = bulkPolicy is not null &&
                PatchSourcePolicy.ThirdPartyConsented(bulkPolicy.Sources, bulkPolicy.AllowThirdPartyApps);
            var skippedThirdParty = 0;
            if (!bulkThirdPartyConsented && thirdPartyTargets.Count > 0)
            {
                skippedThirdParty = thirdPartyTargets.Count;
                thirdPartyTargets.Clear();
            }
            var commandIds = new List<Guid>();

            if (osTargets.Count > 0)
            {
                var created = await PatchOperations.DispatchAsync(
                    dispatcher, reportRepo, cmd.AgentId,
                    PatchCommandPayloads.Install(osTargets, force: false, postAction: null),
                    AutomationExecutionSourceType.PatchInstall,
                    new { mode = "bulk-install", sources = new[] { "os" }, updateKeys = osTargets.Select(update => update.UpdateKey).ToList() },
                    null, ct);
                commandIds.Add(created.Id);
            }

            foreach (var groupBySource in thirdPartyTargets.GroupBy(
                         update => (update.Source ?? string.Empty).ToLowerInvariant()))
            {
                // WU-21: payload leva URL + SHA-256 do catálogo curado.
                var appIds = groupBySource
                    .Select(update => PatchCommandPayloads.ExtractAppId(update.UpdateKey))
                    .ToList();
                var catalogEntries = await catalogRepo.GetActiveEntriesForAsync(groupBySource.Key, appIds, ct);
                var created = await PatchOperations.DispatchAsync(
                    dispatcher, reportRepo, cmd.AgentId,
                    PatchCommandPayloads.InstallThirdParty(
                        groupBySource.Key, groupBySource.ToList(), 1800,
                        PatchCommandPayloads.BuildCatalogMap(catalogEntries)),
                    AutomationExecutionSourceType.PatchInstall,
                    new { mode = "bulk-install", sources = new[] { groupBySource.Key }, updateKeys = groupBySource.Select(update => update.UpdateKey).ToList() },
                    null, ct);
                commandIds.Add(created.Id);
            }

            await patchRepo.UpdateInstallStateAsync(
                cmd.AgentId, targets.Select(update => update.UpdateKey).ToList(),
                PatchUpdateStatus.Installing, null, null, ct);

            return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
                commandIds.Count, cmd.UpdateKeys.Count - targets.Count, commandIds,
                skippedThirdParty > 0
                    ? $"{targets.Count - skippedThirdParty} atualização(ões) enviada(s); {skippedThirdParty} app(s) de terceiros sem duplo consentimento."
                    : $"{targets.Count} atualização(ões) aprovada(s) enviada(s) para instalação."));
        }

        return Result<PatchDispatchDto>.Failure(Error.Validation("action", "Ação inválida. Use approve, deny ou install."));
    }
}

// ── Aprovações e política ─────────────────────────────────────────────────

public sealed class CreatePatchApprovalCommandHandler(
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo
) : IRequestHandler<CreatePatchApprovalCommand, Result<PatchApprovalDto>>
{
    public async Task<Result<PatchApprovalDto>> Handle(CreatePatchApprovalCommand cmd, CancellationToken ct)
    {
        var key = PatchUpdateKeyValidator.IsValid(cmd.UpdateKey)
            ? PatchUpdateKeyValidator.Normalize(cmd.UpdateKey!.Trim())
            : string.Empty;
        if (key.Length == 0)
            return Result<PatchApprovalDto>.Failure(Error.Validation("updateKey", "Chave de atualização inválida."));

        if (cmd.ScopeType != PatchApprovalScopeType.Global && !cmd.ScopeId.HasValue)
            return Result<PatchApprovalDto>.Failure(Error.Validation("scopeId", "Informe o escopo da aprovação."));

        Guid? clientId = null;
        Guid? siteId = null;
        Guid? agentId = null;

        // Popula os ids de cliente/site/agente em todos os níveis: os relatórios
        // filtram regras por clientId e a auditoria usa o escopo completo. Sem
        // isto, uma regra de Site ficava com client_id NULL e sumia do relatório
        // de um cliente específico.
        if (cmd.ScopeType == PatchApprovalScopeType.Client)
        {
            clientId = cmd.ScopeId;
        }
        else if (cmd.ScopeType == PatchApprovalScopeType.Site)
        {
            var site = cmd.ScopeId.HasValue ? await siteRepo.GetByIdAsync(cmd.ScopeId.Value) : null;
            if (site is null)
                return Result<PatchApprovalDto>.Failure(Error.NotFound("Site not found."));
            siteId = site.Id;
            clientId = site.ClientId;
        }
        else if (cmd.ScopeType == PatchApprovalScopeType.Agent)
        {
            var agent = cmd.ScopeId.HasValue ? await agentRepo.GetByIdAsync(cmd.ScopeId.Value) : null;
            if (agent is null)
                return Result<PatchApprovalDto>.Failure(Error.NotFound("Agent not found."));

            var site = await siteRepo.GetByIdAsync(agent.SiteId);
            if (site is null)
                return Result<PatchApprovalDto>.Failure(Error.NotFound("Site not found."));

            agentId = agent.Id;
            siteId = site.Id;
            clientId = site.ClientId;
        }

        var kbId = key.StartsWith("kb:", StringComparison.OrdinalIgnoreCase) ? key[3..] : null;
        var existing = await patchRepo.GetApprovalAsync(cmd.ScopeType, cmd.ScopeId, key, ct);

        var approval = new PatchApproval
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            ScopeType = cmd.ScopeType,
            ScopeId = cmd.ScopeId,
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            UpdateKey = key,
            KbId = kbId,
            Action = cmd.Action,
            Reason = string.IsNullOrWhiteSpace(cmd.Reason) ? null : cmd.Reason.Trim(),
            CreatedBy = existing?.CreatedBy ?? cmd.ChangedBy,
            UpdatedBy = cmd.ChangedBy,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var audit = new PatchApprovalAudit
        {
            Id = Guid.NewGuid(),
            ChangeType = existing is null ? PatchApprovalAuditChangeType.Created : PatchApprovalAuditChangeType.Updated,
            ScopeType = cmd.ScopeType,
            ScopeId = cmd.ScopeId,
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            UpdateKey = key,
            KbId = kbId,
            OldAction = existing?.Action,
            NewAction = cmd.Action,
            Reason = approval.Reason,
            ChangedBy = cmd.ChangedBy,
            IpAddress = cmd.IpAddress,
            ChangedAt = DateTime.UtcNow
        };

        var saved = await patchRepo.UpsertApprovalAsync(approval, audit, ct);
        await resolver.RecomputeForGroupKeyAsync(key, ct);

        return Result<PatchApprovalDto>.Success(PatchDtoMapper.ToDto(saved));
    }
}

public sealed class DeletePatchApprovalCommandHandler(
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver
) : IRequestHandler<DeletePatchApprovalCommand, Result<VoidResult>>
{
    public async Task<Result<VoidResult>> Handle(DeletePatchApprovalCommand cmd, CancellationToken ct)
    {
        var existing = await patchRepo.GetApprovalByIdAsync(cmd.Id, ct);
        if (existing is null)
            return Result<VoidResult>.Failure(Error.NotFound("Aprovação não encontrada."));

        var deleted = await patchRepo.DeleteApprovalAsync(cmd.Id, cmd.ChangedBy, cmd.IpAddress, ct);
        if (!deleted)
            return Result<VoidResult>.Failure(Error.NotFound("Aprovação não encontrada."));

        await resolver.RecomputeForGroupKeyAsync(existing.UpdateKey, ct);
        return Result<VoidResult>.Success(VoidResult.Value);
    }
}

public sealed class UpdatePatchPolicyCommandHandler(
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver
) : IRequestHandler<UpdatePatchPolicyCommand, Result<PatchPolicyDto>>
{
    public async Task<Result<PatchPolicyDto>> Handle(UpdatePatchPolicyCommand cmd, CancellationToken ct)
    {
        if (cmd.ScopeType != PatchApprovalScopeType.Global && !cmd.ScopeId.HasValue)
            return Result<PatchPolicyDto>.Failure(Error.Validation("scopeId", "Informe o escopo da política."));

        if (cmd.ScopeType == PatchApprovalScopeType.Agent)
            return Result<PatchPolicyDto>.Failure(Error.Validation("scopeType",
                "Política de patches é suportada em Global, Cliente ou Site."));

        Guid? clientId = null;
        Guid? siteId = null;
        if (cmd.ScopeType == PatchApprovalScopeType.Client)
            clientId = cmd.ScopeId;
        else if (cmd.ScopeType == PatchApprovalScopeType.Site)
            siteId = cmd.ScopeId;

        var maintenanceWindowJson = cmd.MaintenanceWindowJson;
        if (!string.IsNullOrWhiteSpace(maintenanceWindowJson))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(maintenanceWindowJson);
                if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    return Result<PatchPolicyDto>.Failure(Error.Validation("maintenanceWindowJson",
                        "A janela de manutenção deve ser um objeto JSON."));
            }
            catch (System.Text.Json.JsonException)
            {
                return Result<PatchPolicyDto>.Failure(Error.Validation("maintenanceWindowJson",
                    "A janela de manutenção deve ser um JSON válido."));
            }
        }

        var policy = new PatchPolicy
        {
            Id = Guid.NewGuid(),
            ScopeType = cmd.ScopeType,
            ScopeId = cmd.ScopeId,
            ClientId = clientId,
            SiteId = siteId,
            EnforcementMode = cmd.EnforcementMode,
            AutoApproveClassifications = PatchJson.SerializeStringArray(cmd.AutoApproveClassifications),
            AutoApproveSeverities = PatchJson.SerializeStringArray(cmd.AutoApproveSeverities),
            AutoInstallEnabled = cmd.AutoInstallEnabled,
            MaintenanceWindowJson = string.IsNullOrWhiteSpace(maintenanceWindowJson)
                ? null
                : maintenanceWindowJson,
            RebootPolicy = cmd.RebootPolicy,
            RebootAllowed = cmd.RebootAllowed,
            MaxInstallsPerRun = Math.Clamp(cmd.MaxInstallsPerRun <= 0 ? 50 : cmd.MaxInstallsPerRun, 1, 500),
            HideDeniedUpdates = cmd.HideDeniedUpdates,
            // Deferral: 0..365 dias (fail-closed quando o update não tem data).
            DeferralDays = Math.Clamp(cmd.DeferralDays, 0, 365),
            // Deadline: 0..365 dias; vencido, amplia a janela até o prazo (D03).
            DeadlineDays = Math.Clamp(cmd.DeadlineDays, 0, 365),
            // Origens (D22): allowlist {os, third_party}; ausente = só os.
            Sources = PatchJson.SerializeStringArray(
                (cmd.Sources ?? [])
                    .Where(value => PatchSourcePolicy.Known.Contains(value))
                    .Select(value => value.ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()),
            AllowThirdPartyApps = cmd.AllowThirdPartyApps,
            UpdatedBy = cmd.UpdatedBy,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var saved = await patchRepo.UpsertPolicyAsync(policy, ct);
        await resolver.RecomputeForScopeAsync(cmd.ScopeType, cmd.ScopeId, ct);

        return Result<PatchPolicyDto>.Success(PatchDtoMapper.ToDto(saved));
    }
}

// ── Frota ─────────────────────────────────────────────────────────────────

public sealed class ScanPatchScopeCommandHandler(
    IPatchRepository patchRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<ScanPatchScopeCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(ScanPatchScopeCommand cmd, CancellationToken ct)
    {
        var (clientId, siteId) = PatchScopeResolver.Resolve(cmd.Scope, cmd.ScopeId);
        var scopeAgentIds = await patchRepo.GetAgentIdsInScopeAsync(
            clientId, siteId, cmd.HasGlobalAccess, cmd.AllowedClientIds,
            PatchOperations.MaxCommandsPerBatch, ct);

        var targets = cmd.AgentIds is { Count: > 0 }
            ? scopeAgentIds.Intersect(cmd.AgentIds).Take(PatchOperations.MaxCommandsPerBatch).ToList()
            : scopeAgentIds;

        var commandIds = new List<Guid>();
        foreach (var agentId in targets)
        {
            var created = await PatchOperations.DispatchAsync(
                dispatcher, reportRepo, agentId,
                PatchCommandPayloads.Scan("scope-scan"),
                AutomationExecutionSourceType.PatchScan,
                new { mode = "scan", scope = cmd.Scope.ToString() },
                cmd.CorrelationId, ct);
            commandIds.Add(created.Id);
        }

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            commandIds.Count, targets.Count - commandIds.Count, commandIds,
            $"{commandIds.Count} agente(s) receberam a varredura de atualizações."));
    }
}

public sealed class InstallPatchScopeCommandHandler(
    IPatchRepository patchRepo,
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IConfigurationService configService,
    IAppCatalogRepository catalogRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<InstallPatchScopeCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(InstallPatchScopeCommand cmd, CancellationToken ct)
    {
        if (cmd.UpdateKeys.Count == 0)
            return Result<PatchDispatchDto>.Failure(Error.Validation("updateKeys", "Informe ao menos uma atualização."));

        var postAction = PatchCommandPayloads.NormalizePostAction(cmd.PostAction);
        if (postAction != "none" && !cmd.Confirm)
        {
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "Confirme a ação de energia (reiniciar/desligar) após a instalação."));
        }

        if (cmd.Force && string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<PatchDispatchDto>.Failure(Error.Validation("reason",
                "Informe o motivo para forçar a instalação em lote (registro de auditoria)."));
        }

        // WU-06/D07: sobrepor a janela exige motivo (auditoria).
        if (cmd.OverrideWindow && string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return Result<PatchDispatchDto>.Failure(Error.Validation("reason",
                "Informe o motivo para instalar fora da janela de manutenção (registro de auditoria)."));
        }

        var (clientId, siteId) = PatchScopeResolver.Resolve(cmd.Scope, cmd.ScopeId);
        var scopeAgentIds = await patchRepo.GetAgentIdsInScopeAsync(
            clientId, siteId, cmd.HasGlobalAccess, cmd.AllowedClientIds,
            PatchOperations.MaxCommandsPerBatch, ct);

        var pending = await patchRepo.GetPendingForAgentsAsync(scopeAgentIds, ct);
        var keys = cmd.UpdateKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var commandIds = new List<Guid>();
        var skipped = 0;

        var nowUtc = DateTime.UtcNow;
        var skippedOutsideWindow = 0;
        var skippedNoConsent = 0;

        foreach (var group in pending.GroupBy(update => update.AgentId))
        {
            var targets = group
                .Where(update => keys.Contains(update.UpdateKey) || keys.Contains(update.GroupKey))
                .Where(update => cmd.Force ||
                    update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved)
                .Take(100)
                .ToList();

            if (targets.Count == 0)
            {
                skipped++;
                continue;
            }

            var agent = await agentRepo.GetByIdAsync(group.Key);
            var site = agent is null ? null : await siteRepo.GetByIdAsync(agent.SiteId);
            if (agent is null || site is null)
            {
                skipped++;
                continue;
            }

            var policies = await patchRepo.GetApplicablePoliciesForAgentAsync(site.ClientId, site.Id, ct);
            var policy = policies.OrderByDescending(item => (int)item.ScopeType).FirstOrDefault();

            // WU-19/WU-20 (D22): sem duplo consentimento NÃO se instala app de
            // terceiros — nem por comando manual (o operador pode habilitar na
            // política). OS continua com o comportamento anterior.
            var thirdPartyConsented = policy is not null &&
                PatchSourcePolicy.ThirdPartyConsented(policy.Sources, policy.AllowThirdPartyApps);

            // WU-06/D07: a instalação MANUAL também respeita a janela (antes só o
            // job automático checava). O operador pode sobrepor com motivo —
            // comportamento auditado no relatório de execução.
            if (!cmd.OverrideWindow)
            {
                // Sem política = sem janela configurada: a ação manual do operador
                // não deve ser bloqueada (comportamento anterior preservado).
                if (policy is not null)
                {
                    var siteConfig = await configService.GetSiteConfigAsync(site.Id);
                    var recurringOpen = PatchMaintenanceWindow.IsOpen(
                        policy.MaintenanceWindowJson, nowUtc, siteConfig?.Timezone);

                    DateTime? oldestPublished = null;
                    foreach (var update in targets)
                    {
                        if (!update.PublishedAt.HasValue)
                            continue;
                        if (oldestPublished is null || update.PublishedAt.Value < oldestPublished.Value)
                            oldestPublished = update.PublishedAt.Value;
                    }

                    var window = PatchDeadline.EvaluateWindow(
                        recurringOpen, policy.DeadlineDays, oldestPublished, nowUtc);
                    if (!window.Open)
                    {
                        skippedOutsideWindow++;
                        skipped++;
                        continue;
                    }
                }
            }

            // WU-19/WU-20: OS e apps de terceiros usam comandos diferentes.
            var (osTargets, thirdPartyTargets) = PatchCommandPayloads.SplitBySource(targets);

            if (!thirdPartyConsented && thirdPartyTargets.Count > 0)
            {
                skippedNoConsent += thirdPartyTargets.Count;
                thirdPartyTargets.Clear();
            }

            if (osTargets.Count > 0)
            {
                var created = await PatchOperations.DispatchAsync(
                    dispatcher, reportRepo, group.Key,
                    PatchCommandPayloads.Install(osTargets, cmd.Force, cmd.PostAction),
                    AutomationExecutionSourceType.PatchInstall,
                    new
                    {
                        mode = "scope-install",
                        sources = new[] { "os" },
                        scope = cmd.Scope.ToString(),
                        updateKeys = osTargets.Select(update => update.UpdateKey).ToList(),
                        forced = cmd.Force,
                        postAction,
                        reason = cmd.Reason,
                        requestedBy = cmd.RequestedBy,
                        // D07: instalação fora da janela fica registrada na auditoria.
                        windowOverridden = cmd.OverrideWindow
                    },
                    cmd.CorrelationId, ct);
                commandIds.Add(created.Id);
            }

            foreach (var groupBySource in thirdPartyTargets.GroupBy(
                         update => (update.Source ?? string.Empty).ToLowerInvariant()))
            {
                // WU-21: payload leva URL + SHA-256 do catálogo curado.
                var appIds = groupBySource
                    .Select(update => PatchCommandPayloads.ExtractAppId(update.UpdateKey))
                    .ToList();
                var catalogEntries = await catalogRepo.GetActiveEntriesForAsync(groupBySource.Key, appIds, ct);
                var created = await PatchOperations.DispatchAsync(
                    dispatcher, reportRepo, group.Key,
                    PatchCommandPayloads.InstallThirdParty(
                        groupBySource.Key, groupBySource.ToList(), 1800,
                        PatchCommandPayloads.BuildCatalogMap(catalogEntries)),
                    AutomationExecutionSourceType.PatchInstall,
                    new
                    {
                        mode = "scope-install",
                        sources = new[] { groupBySource.Key },
                        scope = cmd.Scope.ToString(),
                        updateKeys = groupBySource.Select(update => update.UpdateKey).ToList(),
                        forced = cmd.Force,
                        postAction = "none",
                        reason = cmd.Reason,
                        requestedBy = cmd.RequestedBy,
                        windowOverridden = cmd.OverrideWindow
                    },
                    cmd.CorrelationId, ct);
                commandIds.Add(created.Id);
            }

            await patchRepo.UpdateInstallStateAsync(
                group.Key, targets.Select(update => update.UpdateKey).ToList(),
                PatchUpdateStatus.Installing, null, null, ct);
        }

        if (commandIds.Count == 0)
        {
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "Nenhuma atualização selecionada está pendente e aprovada nos agentes deste escopo."));
        }

        var windowNote = skippedOutsideWindow > 0
            ? $" {skippedOutsideWindow} fora da janela de manutenção (use 'overrideWindow' com motivo para instalar mesmo assim)."
            : string.Empty;
        var consentNote = skippedNoConsent > 0
            ? $" {skippedNoConsent} app(s) de terceiros sem duplo consentimento na política (habilite origem + instalação automática)."
            : string.Empty;

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            commandIds.Count, skipped, commandIds,
            $"{commandIds.Count} agente(s) receberam a instalação; {skipped} sem atualizações elegíveis.{windowNote}{consentNote}"));
    }
}
