using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;

namespace Discovery.Infrastructure.Cqrs.Patches;

/// <summary>Operações compartilhadas pelos handlers de patch (aprovação/dispatch).</summary>
internal static class PatchOperations
{
    public const int MaxCommandsPerBatch = 200;

    public static async Task<AgentCommand> DispatchAsync(
        IAgentCommandDispatcher dispatcher,
        IAutomationExecutionReportRepository reportRepo,
        Guid agentId,
        string payload,
        AutomationExecutionSourceType source,
        object metadata,
        string? correlationId,
        CancellationToken ct)
    {
        var command = new AgentCommand
        {
            AgentId = agentId,
            CommandType = CommandType.WindowsUpdate,
            Payload = payload
        };

        var created = await dispatcher.DispatchAsync(command, ct);
        await RunAutomationTaskCommandHandler.CreateReportAsync(
            reportRepo, created, null, null, source, metadata, correlationId);
        return created;
    }

    public static async Task<PatchApprovalDto> SetApprovalAsync(
        IPatchRepository patchRepo,
        IPatchApprovalResolver resolver,
        Agent agent,
        Site site,
        AgentWindowsUpdate update,
        PatchApprovalScopeType scope,
        PatchApprovalActionType action,
        string? reason,
        string? changedBy,
        string? ipAddress,
        Guid? targetScopeId,
        CancellationToken ct)
    {
        var (scopeId, clientId, siteId, agentId) = ResolveScopeTargets(agent, site, scope, targetScopeId);

        var approvalKey = update.GroupKey;
        var kbId = approvalKey.StartsWith("kb:", StringComparison.OrdinalIgnoreCase)
            ? approvalKey[3..]
            : null;

        var existing = await patchRepo.GetApprovalAsync(scope, scopeId, approvalKey, ct);
        var approval = new PatchApproval
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            ScopeType = scope,
            ScopeId = scopeId,
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            UpdateKey = approvalKey,
            KbId = kbId,
            Action = action,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            CreatedBy = existing?.CreatedBy ?? changedBy,
            UpdatedBy = changedBy,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var audit = new PatchApprovalAudit
        {
            Id = Guid.NewGuid(),
            ChangeType = existing is null ? PatchApprovalAuditChangeType.Created : PatchApprovalAuditChangeType.Updated,
            ScopeType = scope,
            ScopeId = scopeId,
            ClientId = clientId,
            SiteId = siteId,
            AgentId = agentId,
            UpdateKey = approvalKey,
            KbId = kbId,
            OldAction = existing?.Action,
            NewAction = action,
            Reason = approval.Reason,
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            ChangedAt = DateTime.UtcNow
        };

        var saved = await patchRepo.UpsertApprovalAsync(approval, audit, ct);

        if (scope == PatchApprovalScopeType.Agent)
            await resolver.RecomputeForAgentAsync(agent.Id, ct);
        else
            await resolver.RecomputeForGroupKeyAsync(approvalKey, ct);

        return PatchDtoMapper.ToDto(saved);
    }

    private static (Guid? ScopeId, Guid? ClientId, Guid? SiteId, Guid? AgentId) ResolveScopeTargets(
        Agent agent,
        Site site,
        PatchApprovalScopeType scope,
        Guid? targetScopeId) => scope switch
    {
        PatchApprovalScopeType.Agent => (agent.Id, site.ClientId, site.Id, agent.Id),
        PatchApprovalScopeType.Site => (targetScopeId ?? site.Id, site.ClientId, site.Id, null),
        PatchApprovalScopeType.Client => (targetScopeId ?? site.ClientId, targetScopeId ?? site.ClientId, null, null),
        _ => (null, null, null, null)
    };
}
