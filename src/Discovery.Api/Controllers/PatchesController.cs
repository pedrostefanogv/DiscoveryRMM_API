using Discovery.Api;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Api.Controllers;

/// <summary>
/// Gerenciador de patches do Windows — visão de frota (global/cliente/site),
/// aprovações, política e ações em lote.
///
/// Toda consulta recebe a access list do usuário e filtra no banco. Nunca
/// confiar apenas no scopeId da query string.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/patches")]
[RequirePermission(ResourceType.Patches, ActionType.View, ScopeSource.AccessList)]
public sealed class PatchesController(
    IMediator mediator,
    IScopeContext scopeContext) : ControllerBase
{
    public sealed record CreateApprovalRequest(
        PatchApprovalScopeType ScopeType,
        Guid? ScopeId,
        string UpdateKey,
        PatchApprovalActionType Action = PatchApprovalActionType.Approve,
        string? Reason = null);

    public sealed record UpdatePolicyRequest(
        PatchEnforcementMode EnforcementMode = PatchEnforcementMode.Monitor,
        IReadOnlyList<string>? AutoApproveClassifications = null,
        IReadOnlyList<string>? AutoApproveSeverities = null,
        bool AutoInstallEnabled = false,
        string? MaintenanceWindowJson = null,
        PatchRebootPolicy RebootPolicy = PatchRebootPolicy.Never,
        bool RebootAllowed = false,
        int MaxInstallsPerRun = 50,
        bool HideDeniedUpdates = false,
        // WU-04/WU-05: retenção por idade e deadline de instalação.
        int DeferralDays = 0,
        int DeadlineDays = 0,
        // WU-19/WU-20/D22: origens e duplo consentimento de terceiros.
        IReadOnlyList<string>? Sources = null,
        bool AllowThirdPartyApps = false);

    public sealed record ScanScopeRequest(
        PatchScope Scope = PatchScope.Global,
        Guid? ScopeId = null,
        IReadOnlyList<Guid>? AgentIds = null);

    public sealed record InstallScopeRequest(
        PatchScope Scope,
        Guid? ScopeId,
        IReadOnlyList<string> UpdateKeys,
        bool Force = false,
        bool Confirm = false,
        string? PostAction = null,
        string? Reason = null,
        // WU-06/D07: sobrepor a janela de manutenção (exige Reason).
        bool OverrideWindow = false);

    // ── Consultas ──────────────────────────────────────────────────────────

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] PatchScope scope = PatchScope.Global,
        [FromQuery] Guid? scopeId = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        var result = await mediator.Send(new GetPatchOverviewQuery(
            scope, scopeId, access.HasGlobalAccess, access.AllowedClientIds), ct);
        return result.ToActionResult();
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> GetSnapshot(
        [FromQuery] PatchScope scope = PatchScope.Global,
        [FromQuery] Guid? scopeId = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        var result = await mediator.Send(new GetPatchSnapshotQuery(
            scope, scopeId, access.HasGlobalAccess, access.AllowedClientIds), ct);
        return result.ToActionResult();
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> GetInventory(
        [FromQuery] PatchScope scope = PatchScope.Global,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 25,
        [FromQuery] string? search = null,
        [FromQuery] string? severity = null,
        [FromQuery] string? status = null,
        [FromQuery] string order = "asc",
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        var result = await mediator.Send(new GetPatchInventoryQuery(
            scope, scopeId, cursor, limit, search, severity, status,
            !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase),
            access.HasGlobalAccess, access.AllowedClientIds), ct);
        return result.ToActionResult();
    }

    [HttpGet("inventory/{groupKey}/agents")]
    public async Task<IActionResult> GetInventoryAgents(
        string groupKey,
        [FromQuery] PatchScope scope = PatchScope.Global,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        var result = await mediator.Send(new GetPatchInventoryAgentsQuery(
            groupKey, scope, scopeId, cursor, limit, status,
            access.HasGlobalAccess, access.AllowedClientIds), ct);
        return result.ToActionResult();
    }

    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(
        [FromQuery] PatchScope scope = PatchScope.Global,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 25,
        [FromQuery] string? search = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        var result = await mediator.Send(new GetPatchPendingQuery(
            scope, scopeId, cursor, limit, search,
            access.HasGlobalAccess, access.AllowedClientIds), ct);
        return result.ToActionResult();
    }

    [HttpGet("compliance")]
    public async Task<IActionResult> GetCompliance(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] int? scanWindowDays = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        var result = await mediator.Send(new GetPatchComplianceQuery(
            clientId, siteId, scanWindowDays ?? 7,
            access.HasGlobalAccess, access.AllowedClientIds), ct);
        return result.ToActionResult();
    }

    // ── Aprovações ─────────────────────────────────────────────────────────

    [HttpGet("approvals")]
    public async Task<IActionResult> GetApprovals(
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        [FromServices] DiscoveryDbContext db,
        [FromQuery] PatchApprovalScopeType? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        if (!access.HasGlobalAccess && scopeType.HasValue && scopeType.Value != PatchApprovalScopeType.Global)
        {
            if (await EnsurePatchScopeAccessAsync(scopeType.Value, scopeId, access, siteRepo, agentRepo, allowGlobalRead: true) is { } denied)
                return denied;
        }

        var result = await mediator.Send(new ListPatchApprovalsQuery(scopeType, scopeId, search, limit), ct);
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        var filtered = await FilterByPatchScopeAsync(
            result.Value!,
            x => (x.ScopeType, x.ScopeId),
            access,
            db,
            ct);
        return Ok(filtered);
    }

    [HttpGet("approvals/audit")]
    public async Task<IActionResult> GetApprovalsAudit(
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        if (!access.HasGlobalAccess)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Auditoria global de aprovações requer acesso global." });

        var result = await mediator.Send(new ListPatchApprovalAuditQuery(limit), ct);
        return result.ToActionResult();
    }

    [HttpPost("approvals")]
    [RequirePermission(ResourceType.Patches, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateApproval(
        [FromBody] CreateApprovalRequest request,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.Edit);
        if (await EnsurePatchScopeAccessAsync(request.ScopeType, request.ScopeId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

        var result = await mediator.Send(new CreatePatchApprovalCommand(
            request.ScopeType,
            request.ScopeId,
            request.UpdateKey,
            request.Action,
            request.Reason,
            CurrentUserId(),
            RemoteIp()), ct);

        return result.Match<IActionResult>(
            success: dto => Ok(dto),
            failure: errors => errors[0].Code switch
            {
                "Validation" => BadRequest(new { error = errors[0].Message }),
                "NotFound" => NotFound(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });
    }

    [HttpDelete("approvals/{ruleId:guid}")]
    [RequirePermission(ResourceType.Patches, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> DeleteApproval(
        Guid ruleId,
        [FromServices] DiscoveryDbContext db,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        CancellationToken ct = default)
    {
        var rule = await db.PatchApprovals.AsNoTracking()
            .Where(r => r.Id == ruleId)
            .Select(r => new { r.ScopeType, r.ScopeId })
            .FirstOrDefaultAsync(ct);
        if (rule is null)
            return NotFound(new { error = "Regra de aprovação não encontrada." });

        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.Delete);
        if (await EnsurePatchScopeAccessAsync(rule.ScopeType, rule.ScopeId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

        var result = await mediator.Send(new DeletePatchApprovalCommand(
            ruleId, CurrentUserId(), RemoteIp()), ct);
        return result.ToActionResult();
    }

    // ── Política ───────────────────────────────────────────────────────────

    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        [FromQuery] PatchApprovalScopeType scopeType = PatchApprovalScopeType.Global,
        [FromQuery] Guid? scopeId = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        if (await EnsurePatchScopeAccessAsync(scopeType, scopeId, access, siteRepo, agentRepo, allowGlobalRead: true) is { } denied)
            return denied;

        var result = await mediator.Send(new GetPatchPolicyQuery(scopeType, scopeId), ct);
        return result.ToActionResult();
    }

    [HttpGet("policies")]
    public async Task<IActionResult> GetPolicies(
        [FromServices] DiscoveryDbContext db,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.View);
        var result = await mediator.Send(new ListPatchPoliciesQuery(), ct);
        if (result.IsFailure || access.HasGlobalAccess)
            return result.ToActionResult();

        var filtered = await FilterByPatchScopeAsync(
            result.Value!,
            x => (x.ScopeType, x.ScopeId),
            access,
            db,
            ct);
        return Ok(filtered);
    }

    [HttpPut("policy")]
    [RequirePermission(ResourceType.Patches, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> UpdatePolicy(
        [FromBody] UpdatePolicyRequest request,
        [FromServices] ISiteRepository siteRepo,
        [FromServices] IAgentRepository agentRepo,
        [FromQuery] PatchApprovalScopeType scopeType = PatchApprovalScopeType.Global,
        [FromQuery] Guid? scopeId = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.Edit);
        if (await EnsurePatchScopeAccessAsync(scopeType, scopeId, access, siteRepo, agentRepo, allowGlobalRead: false) is { } denied)
            return denied;

        var result = await mediator.Send(new UpdatePatchPolicyCommand(
            scopeType,
            scopeId,
            request.EnforcementMode,
            request.AutoApproveClassifications ?? [],
            request.AutoApproveSeverities ?? [],
            request.AutoInstallEnabled,
            request.MaintenanceWindowJson,
            request.RebootPolicy,
            request.RebootAllowed,
            request.MaxInstallsPerRun,
            request.HideDeniedUpdates,
            CurrentUserId(),
            request.DeferralDays,
            request.DeadlineDays,
            request.Sources,
            request.AllowThirdPartyApps), ct);

        return result.Match<IActionResult>(
            success: dto => Ok(dto),
            failure: errors => errors[0].Code switch
            {
                "Validation" => BadRequest(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });
    }

    // ── Ações em lote ──────────────────────────────────────────────────────

    [HttpPost("scan")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Scan([FromBody] ScanScopeRequest? request = null, CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.Execute);
        var result = await mediator.Send(new ScanPatchScopeCommand(
            request?.Scope ?? PatchScope.Global,
            request?.ScopeId,
            request?.AgentIds,
            access.HasGlobalAccess,
            access.AllowedClientIds,
            CorrelationId()), ct);

        return AcceptedOrError(result);
    }

    [HttpPost("install")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> Install([FromBody] InstallScopeRequest request, CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Patches, ActionType.Execute);
        var result = await mediator.Send(new InstallPatchScopeCommand(
            request.Scope,
            request.ScopeId,
            request.UpdateKeys ?? [],
            request.Force,
            request.Confirm,
            request.PostAction,
            access.HasGlobalAccess,
            access.AllowedClientIds,
            CorrelationId(),
            request.Reason,
            CurrentUserId(),
            request.OverrideWindow), ct);

        return AcceptedOrError(result);
    }

    private async Task<IActionResult?> EnsurePatchScopeAccessAsync(
        PatchApprovalScopeType scopeType,
        Guid? scopeId,
        UserScopeAccess access,
        ISiteRepository siteRepo,
        IAgentRepository agentRepo,
        bool allowGlobalRead)
    {
        if (access.HasGlobalAccess)
            return null;

        switch (scopeType)
        {
            case PatchApprovalScopeType.Global:
                return allowGlobalRead
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Apenas administradores globais podem alterar o escopo global." });

            case PatchApprovalScopeType.Client:
                return scopeId.HasValue && access.AllowedClientIds.Contains(scopeId.Value)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este cliente." });

            case PatchApprovalScopeType.Site:
                if (!scopeId.HasValue)
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });
                var site = await siteRepo.GetByIdAsync(scopeId.Value);
                if (site is null)
                    return NotFound(new { error = "Site not found." });
                return TenantScopePolicy.Allows(access, site.ClientId, scopeId.Value)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este site." });

            case PatchApprovalScopeType.Agent:
                if (!scopeId.HasValue)
                    return StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });
                var agent = await agentRepo.GetByIdAsync(scopeId.Value);
                if (agent is null)
                    return NotFound(new { error = "Agent not found." });
                var agentSite = await siteRepo.GetByIdAsync(agent.SiteId);
                return TenantScopePolicy.Allows(access, agentSite?.ClientId ?? Guid.Empty, agent.SiteId)
                    ? null
                    : StatusCode(StatusCodes.Status403Forbidden, new { error = "Sem acesso a este agente." });

            default:
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "Escopo não autorizado." });
        }
    }

    private static async Task<IReadOnlyList<T>> FilterByPatchScopeAsync<T>(
        IReadOnlyList<T> items,
        Func<T, (PatchApprovalScopeType scopeType, Guid? scopeId)> selector,
        UserScopeAccess access,
        DiscoveryDbContext db,
        CancellationToken ct)
    {
        var siteIds = new HashSet<Guid>();
        var agentIds = new HashSet<Guid>();
        foreach (var item in items)
        {
            var (st, sid) = selector(item);
            if (!sid.HasValue) continue;
            if (st == PatchApprovalScopeType.Site) siteIds.Add(sid.Value);
            else if (st == PatchApprovalScopeType.Agent) agentIds.Add(sid.Value);
        }

        var agentSiteMap = agentIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Agents.AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.SiteId, ct);

        foreach (var sId in agentSiteMap.Values)
            siteIds.Add(sId);

        var siteClientMap = siteIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Sites.AsNoTracking()
                .Where(s => siteIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.ClientId, ct);

        return items.Where(item =>
        {
            var (st, sid) = selector(item);
            return st switch
            {
                PatchApprovalScopeType.Global => true,
                PatchApprovalScopeType.Client => sid.HasValue && access.AllowedClientIds.Contains(sid.Value),
                PatchApprovalScopeType.Site => sid.HasValue && TenantScopePolicy.Allows(access, siteClientMap.GetValueOrDefault(sid.Value), sid.Value),
                PatchApprovalScopeType.Agent => sid.HasValue && agentSiteMap.TryGetValue(sid.Value, out var aSiteId)
                    && TenantScopePolicy.Allows(access, siteClientMap.GetValueOrDefault(aSiteId), aSiteId),
                _ => false
            };
        }).ToList();
    }

    private IActionResult AcceptedOrError(Discovery.Core.Cqrs.Result<PatchDispatchDto> result)
        => result.Match<IActionResult>(
            success: dto => Accepted(new
            {
                success = true,
                dispatched = dto.Dispatched,
                skipped = dto.Skipped,
                commandIds = dto.CommandIds,
                message = dto.Message
            }),
            failure: errors => errors[0].Code switch
            {
                "Conflict" => Conflict(new { requiresConfirmation = true, error = errors[0].Message }),
                "Validation" => BadRequest(new { error = errors[0].Message }),
                "NotFound" => NotFound(new { error = errors[0].Message }),
                _ => BadRequest(new { error = errors[0].Message })
            });

    private string? CorrelationId()
        => Request.Headers.TryGetValue("X-Correlation-Id", out var values) ? values.FirstOrDefault() : null;

    private string? CurrentUserId()
        => HttpContext.Items["UserId"] is Guid userId ? $"user:{userId}" : null;

    private string? RemoteIp()
        => HttpContext.Connection.RemoteIpAddress?.ToString();
}
