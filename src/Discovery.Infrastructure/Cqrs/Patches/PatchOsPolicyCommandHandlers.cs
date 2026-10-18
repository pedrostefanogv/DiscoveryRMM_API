using System.Text.Json;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Patches;

/// <summary>
/// Enforcement no SO (opt-in): oculta os KBs recusados e aplica a política de
/// Windows Update (notificar/deferir). Só roda quando a política do escopo está
/// em <see cref="PatchEnforcementMode.OsEnforced"/>. É best-effort: GPO/Intune
/// podem sobrepor. O backup devolvido pelo agente é gravado no patch_os_states
/// quando o resultado do comando chega.
/// </summary>
public sealed class ApplyAgentPatchOsPolicyCommandHandler(
    IAgentRepository agentRepo,
    ISiteRepository siteRepo,
    IPatchRepository patchRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<ApplyAgentPatchOsPolicyCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(ApplyAgentPatchOsPolicyCommand cmd, CancellationToken ct)
    {
        var agent = await agentRepo.GetByIdAsync(cmd.AgentId);
        if (agent is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Agent not found."));

        var site = await siteRepo.GetByIdAsync(agent.SiteId);
        if (site is null)
            return Result<PatchDispatchDto>.Failure(Error.NotFound("Site not found."));

        var policies = await patchRepo.GetApplicablePoliciesForAgentAsync(site.ClientId, site.Id, ct);
        var policy = policies.OrderByDescending(item => (int)item.ScopeType).FirstOrDefault();
        if (policy is null || policy.EnforcementMode != PatchEnforcementMode.OsEnforced)
        {
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "Enforcement no SO não está habilitado na política deste escopo (modo OsEnforced)."));
        }

        var commandIds = new List<Guid>();
        var hiddenKeys = new List<string>();

        if (cmd.HideDenied)
        {
            var pending = await patchRepo.GetPendingForAgentAsync(cmd.AgentId, ct);
            var denied = pending
                .Where(update => update.ApprovalState == PatchApprovalState.Denied)
                .Take(100)
                .ToList();
            hiddenKeys.AddRange(denied.Select(update => update.UpdateKey));

            if (hiddenKeys.Count > 0)
            {
                var hideCommand = await PatchOperations.DispatchAsync(
                    dispatcher, reportRepo, cmd.AgentId,
                    PatchCommandPayloads.Hide(hiddenKeys, hidden: true),
                    AutomationExecutionSourceType.PatchOsPolicy,
                    new { mode = "os-hide-denied", count = hiddenKeys.Count, requestedBy = cmd.RequestedBy },
                    null, ct);
                commandIds.Add(hideCommand.Id);
            }
        }

        var settings = new Dictionary<string, object?>
        {
            ["noAutoUpdate"] = 0,
            ["auOptions"] = 2,
            ["deferQualityUpdates"] = 1,
            ["deferQualityUpdatesPeriodInDays"] = 7,
            ["pauseQualityUpdates"] = 0
        };

        var policyCommand = await PatchOperations.DispatchAsync(
            dispatcher, reportRepo, cmd.AgentId,
            PatchCommandPayloads.PolicyApply(settings),
            AutomationExecutionSourceType.PatchOsPolicy,
            new { mode = "os-policy-apply", requestedBy = cmd.RequestedBy },
            null, ct);
        commandIds.Add(policyCommand.Id);

        await patchRepo.SaveOsStateAsync(new PatchOsState
        {
            Id = Guid.NewGuid(),
            AgentId = cmd.AgentId,
            CommandId = policyCommand.Id,
            AppliedBy = cmd.RequestedBy,
            AppliedAt = DateTime.UtcNow,
            PreviousJson = null,
            AppliedJson = JsonSerializer.Serialize(new { hiddenKeys, settings })
        }, ct);

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            commandIds.Count, 0, commandIds,
            "Enforcement no SO aplicado (best-effort). O backup será gravado quando o agente responder."));
    }
}

/// <summary>Reverte o enforcement no SO usando o backup gravado no resultado do agente.</summary>
public sealed class RollbackAgentPatchOsPolicyCommandHandler(
    IPatchRepository patchRepo,
    IAgentCommandDispatcher dispatcher,
    IAutomationExecutionReportRepository reportRepo
) : IRequestHandler<RollbackAgentPatchOsPolicyCommand, Result<PatchDispatchDto>>
{
    public async Task<Result<PatchDispatchDto>> Handle(RollbackAgentPatchOsPolicyCommand cmd, CancellationToken ct)
    {
        var state = await patchRepo.GetLatestOsStateAsync(cmd.AgentId, ct);
        if (state is null)
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "Nenhum enforcement no SO aplicado para este agente."));

        if (string.IsNullOrWhiteSpace(state.PreviousJson))
        {
            return Result<PatchDispatchDto>.Failure(Error.Conflict(
                "O agente ainda não devolveu o backup da política. Aguarde o resultado do comando de aplicação."));
        }

        var commandIds = new List<Guid>();

        var hiddenKeys = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.AppliedJson))
        {
            try
            {
                using var document = JsonDocument.Parse(state.AppliedJson);
                if (document.RootElement.TryGetProperty("hiddenKeys", out var keysElement) &&
                    keysElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in keysElement.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            var key = item.GetString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(key))
                                hiddenKeys.Add(key);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // applied_json legado/inválido: segue sem unhide.
            }
        }

        if (hiddenKeys.Count > 0)
        {
            var unhideCommand = await PatchOperations.DispatchAsync(
                dispatcher, reportRepo, cmd.AgentId,
                PatchCommandPayloads.Hide(hiddenKeys, hidden: false),
                AutomationExecutionSourceType.PatchOsPolicy,
                new { mode = "os-unhide", count = hiddenKeys.Count, requestedBy = cmd.RequestedBy },
                null, ct);
            commandIds.Add(unhideCommand.Id);
        }

        var rollbackCommand = await PatchOperations.DispatchAsync(
            dispatcher, reportRepo, cmd.AgentId,
            PatchCommandPayloads.PolicyRollback(state.PreviousJson),
            AutomationExecutionSourceType.PatchOsPolicy,
            new { mode = "os-policy-rollback", requestedBy = cmd.RequestedBy },
            null, ct);
        commandIds.Add(rollbackCommand.Id);

        await patchRepo.MarkOsStateRevertedAsync(state.Id, cmd.RequestedBy, ct);

        return Result<PatchDispatchDto>.Success(new PatchDispatchDto(
            commandIds.Count, 0, commandIds,
            "Rollback do enforcement enviado ao agente."));
    }
}
