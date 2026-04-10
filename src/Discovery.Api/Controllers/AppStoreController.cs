using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/app-store")]
public class AppStoreController : ControllerBase
{
    private readonly IAppStoreService _appStoreService;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IAppCatalogSyncService _appCatalogSyncService;
    private readonly ISyncInvalidationPublisher _syncInvalidationPublisher;

    public AppStoreController(
        IAppStoreService appStoreService,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository,
        IAppCatalogSyncService appCatalogSyncService,
        ISyncInvalidationPublisher syncInvalidationPublisher)
    {
        _appStoreService = appStoreService;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
        _appCatalogSyncService = appCatalogSyncService;
        _syncInvalidationPublisher = syncInvalidationPublisher;
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> SearchCatalog(
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        [FromQuery] string? search = null,
        [FromQuery] string? architecture = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _appStoreService.SearchCatalogAsync(
            installationType,
            search,
            architecture,
            limit,
            cursor,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("catalog/{packageId}")]
    public async Task<IActionResult> GetCatalogPackage(
        string packageId,
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        CancellationToken cancellationToken = default)
    {
        var item = await _appStoreService.GetCatalogPackageByIdAsync(installationType, packageId, cancellationToken);
        if (item is null)
            return NotFound(new { error = "Package not found." });

        return Ok(item);
    }

    [HttpPost("catalog/custom")]
    public async Task<IActionResult> UpsertCustomCatalogPackage(
        [FromBody] UpsertCustomAppCatalogPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = await _appStoreService.UpsertCustomCatalogPackageAsync(request, cancellationToken);
            await _syncInvalidationPublisher.PublishGlobalAsync(
                SyncResourceType.AppStore,
                "app-store-custom-catalog-upsert",
                AppInstallationType.Custom,
                cancellationToken: cancellationToken);
            return Ok(item);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("approvals")]
    public async Task<IActionResult> GetApprovals(
        [FromQuery] AppApprovalScopeType scopeType,
        [FromQuery] Guid? scopeId,
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        CancellationToken cancellationToken = default)
    {
        var rules = await _appStoreService.GetRulesByScopeAsync(scopeType, scopeId, installationType, cancellationToken);
        return Ok(new
        {
            scopeType,
            scopeId,
            installationType,
            count = rules.Count,
            items = rules
        });
    }

    [HttpPost("approvals")]
    public async Task<IActionResult> UpsertApproval(
        [FromBody] UpsertAppApprovalRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rule = await _appStoreService.UpsertRuleAsync(
                request.ScopeType,
                request.ScopeId,
                request.InstallationType,
                request.PackageId,
                request.Action,
                request.AutoUpdateEnabled,
                request.Reason,
                HttpContext.Items["Username"] as string ?? "api",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);

            await _syncInvalidationPublisher.PublishByScopeAsync(
                SyncResourceType.AppStore,
                request.ScopeType,
                request.ScopeId,
                "app-store-approval-upsert",
                request.InstallationType,
                cancellationToken: cancellationToken);

            return Ok(rule);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("approvals/audit")]
    public async Task<IActionResult> GetApprovalAudit(
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        [FromQuery] string? packageId = null,
        [FromQuery] AppApprovalScopeType? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] string? changedBy = null,
        [FromQuery] DateTime? changedFrom = null,
        [FromQuery] DateTime? changedTo = null,
        [FromQuery] AppApprovalAuditChangeType? changeType = null,
        [FromQuery] int limit = 100,
        [FromQuery] Guid? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (changedFrom.HasValue && changedTo.HasValue && changedFrom > changedTo)
            return BadRequest(new { error = "changedFrom must be less than or equal to changedTo." });

        var page = await _appStoreService.GetAuditHistoryAsync(
            installationType,
            packageId,
            scopeType,
            scopeId,
            changedBy,
            changedFrom,
            changedTo,
            changeType,
            limit,
            cursor,
            cancellationToken);

        return Ok(page);
    }

    [HttpGet("diff/effective")]
    public async Task<IActionResult> GetEffectiveDiffs(
        [FromQuery] AppApprovalScopeType scopeType,
        [FromQuery] Guid? scopeId,
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _appStoreService.GetEffectiveAppDiffsAsync(
                scopeType,
                scopeId,
                installationType,
                search,
                limit,
                cursor,
                cancellationToken);

            return Ok(page);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("approvals/{ruleId:guid}")]
    public async Task<IActionResult> DeleteApproval(
        Guid ruleId,
        [FromQuery] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await _appStoreService.DeleteRuleAsync(
            ruleId,
            reason,
            HttpContext.Items["Username"] as string ?? "api",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);

        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AppStore,
            "app-store-approval-deleted",
            cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpGet("diff/{packageId}")]
    public async Task<IActionResult> GetPackageDiff(
        string packageId,
        [FromQuery] AppApprovalScopeType scopeType,
        [FromQuery] Guid? scopeId,
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var diff = await _appStoreService.GetPackageDiffAsync(
                scopeType,
                scopeId,
                installationType,
                packageId,
                cancellationToken);

            return Ok(diff);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("effective")]
    public async Task<IActionResult> GetEffectiveByScope(
        [FromQuery] AppApprovalScopeType scopeType,
        [FromQuery] Guid? scopeId,
        [FromQuery] AppInstallationType installationType = AppInstallationType.Winget,
        [FromQuery] string? search = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _appStoreService.GetEffectiveAppsPageAsync(
                scopeType,
                scopeId,
                installationType,
                search,
                limit,
                cursor,
                cancellationToken);

            return Ok(page);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncCatalog(
        [FromQuery] AppInstallationType installationType,
        CancellationToken cancellationToken = default)
    {
        var result = await _appCatalogSyncService.SyncCatalogAsync(installationType, cancellationToken);

        if (!result.Success && result.Error?.Contains("not supported", StringComparison.OrdinalIgnoreCase) == true)
            return BadRequest(result);

        if (!result.Success && result.Error?.Contains("already in progress") == true)
            return StatusCode(409, result);

        if (!result.Success && result.Error?.Contains("resume from the last successful page", StringComparison.OrdinalIgnoreCase) == true)
            return StatusCode(202, result);

        if (!result.Success)
            return StatusCode(500, result);

        await _syncInvalidationPublisher.PublishGlobalAsync(
            SyncResourceType.AppStore,
            "app-store-catalog-synced",
            installationType,
            cancellationToken: cancellationToken);

        return Ok(result);
    }
}

public record UpsertAppApprovalRuleRequest(
    AppApprovalScopeType ScopeType,
    Guid? ScopeId,
    AppInstallationType InstallationType,
    string PackageId,
    AppApprovalActionType Action,
    bool? AutoUpdateEnabled,
    string? Reason);
