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
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? search = null, [FromQuery] string? architecture = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new SearchAppStoreQuery(search, architecture, cursor, limit));
        return result.ToActionResult();
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> GetCatalog([FromQuery] int installationType = 0, [FromQuery] string? search = null, [FromQuery] string? architecture = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 20)
    {
        var result = await mediator.Send(new GetAppStoreCatalogQuery(installationType, search, architecture, cursor, limit));
        return result.ToActionResult();
    }

    [HttpGet("effective")]
    public async Task<IActionResult> GetEffective([FromQuery] Guid? clientId = null, [FromQuery] Guid? siteId = null, [FromQuery] Guid? agentId = null)
    {
        var result = await mediator.Send(new GetAppStoreEffectiveAppsQuery(clientId, siteId, agentId));
        return result.ToActionResult();
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncCatalog([FromQuery] int installationType = 0)
    {
        var result = await mediator.Send(new SyncAppStoreCatalogCommand((AppInstallationType)installationType));
        return result.ToActionResult();
    }

    [HttpGet("approvals")]
    public async Task<IActionResult> GetApprovals(
        [FromQuery] int? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] int? installationType = null)
    {
        var sc = scopeType.HasValue ? (AppApprovalScopeType)scopeType.Value : default(AppApprovalScopeType?);
        var inst = installationType;
        var result = await mediator.Send(new GetAppStoreApprovalsQuery(sc, scopeId, inst));
        return result.ToActionResult();
    }

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
