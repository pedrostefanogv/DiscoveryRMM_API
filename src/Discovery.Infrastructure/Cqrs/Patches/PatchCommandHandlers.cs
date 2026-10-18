using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Patches;

// ── Agente ────────────────────────────────────────────────────────────────

public sealed class ScanAgentWindowsUpdatesCommandHandler(
    IAgentRepository agentRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<ScanAgentWindowsUpdatesCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(ScanAgentWindowsUpdatesCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Agent not found."));

        var created = await PatchOperations.DispatchAsync(
            dispatcher, reportRepo, cmd.AgentId,
            PatchCommandPayloads.Scan("on-demand"),
            AutomationExecutionSourceType.PatchScan,
            new { mode = "scan", reason = "on-demand" },
            cmd.CorrelationId, ct);

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            1, 0, [created.Id], "Varredura de atualizações enviada ao agente."));
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
    IPatchApprovalResolver resolver
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

        return Result<PatchApprovalDto>.Success(dto);
    }
}

public sealed class BulkAgentWindowsUpdateCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
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

            var payload = PatchCommandPayloads.Install(targets, force: false, postAction: null);
            var created = await PatchOperations.DispatchAsync(
                dispatcher, reportRepo, cmd.AgentId, payload,
                AutomationExecutionSourceType.PatchInstall,
                new { mode = "bulk-install", updateKeys = targets.Select(update => update.UpdateKey).ToList() },
                null, ct);

            await patchRepo.UpdateInstallStateAsync(
                cmd.AgentId, targets.Select(update => update.UpdateKey).ToList(),
                PatchUpdateStatus.Installing, null, null, ct);

            return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
                1, cmd.UpdateKeys.Count - targets.Count, [created.Id],
                $"{targets.Count} atualização(ões) aprovada(s) enviada(s) para instalação."));
        }

        return Result<PatchDispatchDto>.Failure(Error.Validation("action", "Ação inválida. Use approve, deny ou install."));
    }
}

// ── Aprovações e política ─────────────────────────────────────────────────

public sealed class CreatePatchApprovalCommandHandler(
    IPatchRepository patchRepo,
    IPatchApprovalResolver resolver
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
        if (cmd.ScopeType == PatchApprovalScopeType.Client)
            clientId = cmd.ScopeId;
        else if (cmd.ScopeType == PatchApprovalScopeType.Site)
            siteId = cmd.ScopeId;
        else if (cmd.ScopeType == PatchApprovalScopeType.Agent)
            agentId = cmd.ScopeId;

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

        var (clientId, siteId) = PatchScopeResolver.Resolve(cmd.Scope, cmd.ScopeId);
        var scopeAgentIds = await patchRepo.GetAgentIdsInScopeAsync(
            clientId, siteId, cmd.HasGlobalAccess, cmd.AllowedClientIds,
            PatchOperations.MaxCommandsPerBatch, ct);

        var pending = await patchRepo.GetPendingForAgentsAsync(scopeAgentIds, ct);
        var keys = cmd.UpdateKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var commandIds = new List<Guid>();
        var skipped = 0;

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

            var payload = PatchCommandPayloads.Install(targets, cmd.Force, cmd.PostAction);
            var created = await PatchOperations.DispatchAsync(
                dispatcher, reportRepo, group.Key, payload,
                AutomationExecutionSourceType.PatchInstall,
                new
                {
                    mode = "scope-install",
                    scope = cmd.Scope.ToString(),
                    updateKeys = targets.Select(update => update.UpdateKey).ToList(),
                    forced = cmd.Force,
                    postAction,
                    reason = cmd.Reason,
                    requestedBy = cmd.RequestedBy
                },
                cmd.CorrelationId, ct);

            await patchRepo.UpdateInstallStateAsync(
                group.Key, targets.Select(update => update.UpdateKey).ToList(),
                PatchUpdateStatus.Installing, null, null, ct);

            commandIds.Add(created.Id);
        }

        if (commandIds.Count == 0)
        {
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "Nenhuma atualização selecionada está pendente e aprovada nos agentes deste escopo."));
        }

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            commandIds.Count, skipped, commandIds,
            $"{commandIds.Count} agente(s) receberam a instalação; {skipped} sem atualizações elegíveis."));
    }
}
