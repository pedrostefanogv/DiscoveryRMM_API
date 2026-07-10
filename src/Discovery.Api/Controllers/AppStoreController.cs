using Discovery.Core.Cqrs.AppStore.Commands;
using Discovery.Core.Cqrs.AppStore.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/app-store")]
public class AppStoreController(IMediator mediator) : ControllerBase
{
    // ═══════════════════════════════════════════════════════════════
    // Catalog
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Search/browse catalog by installation type (0=Winget, 1=Chocolatey, 2=Custom).</summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] int installationType = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? architecture = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new SearchAppStoreQuery(
            (AppInstallationType)installationType, search, architecture, cursor, limit));
        return result.ToActionResult();
    }

    /// <summary>List catalog with pagination by cursor.</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> GetCatalog(
        [FromQuery] int installationType = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? architecture = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new GetAppStoreCatalogQuery(installationType, search, architecture, cursor, limit));
        return result.ToActionResult();
    }

    /// <summary>Get a single package by ID and installation type.</summary>
    [HttpGet("catalog/{packageId}")]
    public async Task<IActionResult> GetPackageById(
        string packageId,
        [FromQuery] int installationType = 0)
    {
        var result = await mediator.Send(new GetCatalogPackageByIdQuery(installationType, packageId));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound" ? NotFound() : BadRequest());
    }

    // ═══════════════════════════════════════════════════════════════
    // Custom packages (CRUD)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Create or update a custom package (InstallationType=Custom).</summary>
    [HttpPost("custom")]
    public async Task<IActionResult> UpsertCustomPackage([FromBody] UpsertCustomAppPackageCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    // ═══════════════════════════════════════════════════════════════
    // Sync
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Sync catalog from Winget (0) or Chocolatey (1). Custom (2) is manual.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> SyncCatalog([FromQuery] int installationType = 0)
    {
        var result = await mediator.Send(new SyncAppStoreCatalogCommand((AppInstallationType)installationType));
        return result.ToActionResult();
    }

    // ═══════════════════════════════════════════════════════════════
    // Effective (approved) apps
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get approved/effective apps for a scope.</summary>
    [HttpGet("effective")]
    public async Task<IActionResult> GetEffective(
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] int installationType = 0)
    {
        var result = await mediator.Send(new GetAppStoreEffectiveAppsQuery(
            clientId, siteId, agentId, (AppInstallationType)installationType));
        return result.ToActionResult();
    }

    // ═══════════════════════════════════════════════════════════════
    // Approval rules
    // ═══════════════════════════════════════════════════════════════

    /// <summary>List approval rules by scope and installation type.</summary>
    [HttpGet("approvals")]
    public async Task<IActionResult> GetApprovals(
        [FromQuery] int? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] int? installationType = null)
    {
        var sc = scopeType.HasValue ? (AppApprovalScopeType)scopeType.Value : default(AppApprovalScopeType?);
        var result = await mediator.Send(new GetAppStoreApprovalsQuery(sc, scopeId, installationType));
        return result.ToActionResult();
    }

    /// <summary>Create/update an approval rule.</summary>
    [HttpPost("approvals")]
    public async Task<IActionResult> UpsertApproval([FromBody] UpsertAppApprovalRuleCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Delete an approval rule.</summary>
    [HttpDelete("approvals/{ruleId:guid}")]
    public async Task<IActionResult> DeleteApproval(
        Guid ruleId,
        [FromQuery] string? reason = null,
        [FromQuery] string? changedBy = null,
        [FromQuery] string? ipAddress = null)
    {
        var result = await mediator.Send(new DeleteAppApprovalRuleCommand(ruleId, reason, changedBy, ipAddress));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // ═══════════════════════════════════════════════════════════════
    // Audit
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Audit history for approval changes.</summary>
    [HttpGet("approvals/audit")]
    public async Task<IActionResult> GetApprovalAudit(
        [FromQuery] int? installationType = null,
        [FromQuery] string? packageId = null,
        [FromQuery] int? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? changedBy = null,
        [FromQuery] DateTime? changedFrom = null,
        [FromQuery] DateTime? changedTo = null,
        [FromQuery] int? changeType = null,
        [FromQuery] int limit = 50,
        [FromQuery] Guid? cursor = null)
    {
        var sc = scopeType.HasValue ? (AppApprovalScopeType)scopeType.Value : default(AppApprovalScopeType?);
        var ct = changeType.HasValue ? (AppApprovalAuditChangeType)changeType.Value : default(AppApprovalAuditChangeType?);
        var result = await mediator.Send(new GetAppStoreApprovalAuditQuery(
            installationType, packageId, sc, scopeId, changedBy, changedFrom, changedTo, ct, limit, cursor));
        return result.ToActionResult();
    }

    // ═══════════════════════════════════════════════════════════════
    // Package diff
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Get diff for a single package across scope hierarchy.</summary>
    [HttpGet("diff/{packageId}")]
    public async Task<IActionResult> GetPackageDiff(
        string packageId,
        [FromQuery] int installationType = 0,
        [FromQuery] int scopeType = 0,
        [FromQuery] Guid? scopeId = null)
    {
        var result = await mediator.Send(new GetAppStorePackageDiffQuery(
            (AppInstallationType)installationType, packageId, (AppApprovalScopeType)scopeType, scopeId));
        return result.ToActionResult();
    }

    /// <summary>Get effective diffs across a scope.</summary>
    [HttpGet("diff/effective")]
    public async Task<IActionResult> GetEffectiveDiffs(
        [FromQuery] int scopeType = 0,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] int installationType = 0,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null)
    {
        var result = await mediator.Send(new GetAppStoreEffectiveDiffsQuery(
            (AppApprovalScopeType)scopeType, scopeId, installationType, search, limit, cursor));
        return result.ToActionResult();
    }
}
