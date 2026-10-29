using Discovery.Api;
using Discovery.Api.Filters;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using MediatR;
using Microsoft.AspNetCore.Mvc;

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
[RequirePermission(ResourceType.Patches, ActionType.View)]
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
        [FromQuery] PatchApprovalScopeType? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new ListPatchApprovalsQuery(scopeType, scopeId, search, limit), ct);
        return result.ToActionResult();
    }

    [HttpGet("approvals/audit")]
    public async Task<IActionResult> GetApprovalsAudit(
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new ListPatchApprovalAuditQuery(limit), ct);
        return result.ToActionResult();
    }

    [HttpPost("approvals")]
    [RequirePermission(ResourceType.Patches, ActionType.Edit)]
    public async Task<IActionResult> CreateApproval(
        [FromBody] CreateApprovalRequest request,
        CancellationToken ct = default)
    {
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
    [RequirePermission(ResourceType.Patches, ActionType.Delete)]
    public async Task<IActionResult> DeleteApproval(Guid ruleId, CancellationToken ct = default)
    {
        var result = await mediator.Send(new DeletePatchApprovalCommand(
            ruleId, CurrentUserId(), RemoteIp()), ct);
        return result.ToActionResult();
    }

    // ── Política ───────────────────────────────────────────────────────────

    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(
        [FromQuery] PatchApprovalScopeType scopeType = PatchApprovalScopeType.Global,
        [FromQuery] Guid? scopeId = null,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new GetPatchPolicyQuery(scopeType, scopeId), ct);
        return result.ToActionResult();
    }

    [HttpGet("policies")]
    public async Task<IActionResult> GetPolicies(CancellationToken ct = default)
    {
        var result = await mediator.Send(new ListPatchPoliciesQuery(), ct);
        return result.ToActionResult();
    }

    [HttpPut("policy")]
    [RequirePermission(ResourceType.Patches, ActionType.Edit)]
    public async Task<IActionResult> UpdatePolicy(
        [FromBody] UpdatePolicyRequest request,
        [FromQuery] PatchApprovalScopeType scopeType = PatchApprovalScopeType.Global,
        [FromQuery] Guid? scopeId = null,
        CancellationToken ct = default)
    {
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

    // ── Catálogo de apps de terceiros (WU-21/D24) ─────────────────────────

    public sealed record UpsertCatalogEntryRequest(
        string Source,
        string PackageId,
        string Name,
        string Version,
        string InstallerUrl,
        string Sha256,
        string? Publisher = null,
        string? InstallerType = null,
        string? SilentArgs = null,
        string? ProductCode = null,
        DateTime? ReleaseDate = null,
        string? Notes = null);

    public sealed record RevokeCatalogEntryRequest(string? Reason = null);

    [HttpGet("app-catalog")]
    public async Task<IActionResult> ListCatalog(
        [FromQuery] string? source = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new ListAppCatalogEntriesQuery(source, status, search, limit), ct);
        return result.Match<IActionResult>(
            success: dto => Ok(dto),
            failure: errors => BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("app-catalog")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> UpsertCatalogEntry(
        [FromBody] UpsertCatalogEntryRequest request,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new UpsertAppCatalogEntryCommand(
            request.Source,
            request.PackageId,
            request.Name,
            request.Version,
            request.InstallerUrl,
            request.Sha256,
            request.Publisher,
            request.InstallerType,
            request.SilentArgs,
            request.ProductCode,
            request.ReleaseDate,
            request.Notes,
            CurrentUserId()), ct);

        return result.Match<IActionResult>(
            success: dto => Ok(dto),
            failure: errors => errors[0].Code == "Validation"
                ? BadRequest(new { error = errors[0].Message })
                : BadRequest(new { error = errors[0].Message }));
    }

    [HttpPost("app-catalog/{id:guid}/revoke")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
    public async Task<IActionResult> RevokeCatalogEntry(
        Guid id,
        [FromBody] RevokeCatalogEntryRequest? request = null,
        CancellationToken ct = default)
    {
        var result = await mediator.Send(new RevokeAppCatalogEntryCommand(id, CurrentUserId(), request?.Reason), ct);
        return result.Match<IActionResult>(
            success: dto => Ok(dto),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { error = errors[0].Message })
                : BadRequest(new { error = errors[0].Message }));
    }

    // ── Ações em lote ──────────────────────────────────────────────────────

    [HttpPost("scan")]
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
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
    [RequirePermission(ResourceType.Patches, ActionType.Execute)]
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
