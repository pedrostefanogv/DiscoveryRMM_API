using Discovery.Api.Filters;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/files")]
public class ManagedFilesController(
    IManagedFileService fileService,
    IFileLinkService linkService,
    IScopeContext scopeContext,
    ISiteRepository siteRepo) : ControllerBase
{
    private string? CurrentUser => HttpContext.Items["Username"] as string;

    [HttpGet]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> List([FromQuery] ManagedFileFilter filter, CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.View);
        var (allowedClients, allowedSites) = access.HasGlobalAccess
            ? (null, null)
            : (access.AllowedClientIds, access.AllowedSiteIds);

        var result = await fileService.ListAsync(filter, allowedClients, allowedSites, ct);
        return Ok(result);
    }

    [HttpGet("stats")]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.View);
        var (allowedClients, allowedSites) = access.HasGlobalAccess
            ? (null, null)
            : (access.AllowedClientIds, access.AllowedSiteIds);

        return Ok(await fileService.GetStatsAsync(allowedClients, allowedSites, ct));
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var file = await fileService.GetByIdAsync(id, ct);
        if (file is null)
            return NotFound(new { error = "Arquivo não encontrado." });

        if (!await CanAccessFileAsync(file, ActionType.View))
            return Forbid();

        return Ok(file);
    }

    [HttpPost("upload/begin")]
    [RequirePermission(ResourceType.Files, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> BeginUpload([FromBody] BeginManagedFileUploadRequest request, CancellationToken ct)
    {
        if (!await CanAccessScopeAsync(request.ClientId, request.SiteId, ActionType.Create))
            return Forbid();

        try
        {
            return Ok(await fileService.BeginUploadAsync(request, CurrentUser, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("upload/part-url")]
    [RequirePermission(ResourceType.Files, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> GetPartUrl(
        [FromQuery] string objectKey,
        [FromQuery] string uploadId,
        [FromQuery] int partNumber,
        CancellationToken ct)
    {
        try
        {
            return Ok(await fileService.GetUploadPartUrlAsync(objectKey, uploadId, partNumber, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("upload/complete")]
    [RequirePermission(ResourceType.Files, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> CompleteUpload([FromBody] CompleteManagedFileUploadRequest request, CancellationToken ct)
    {
        if (!await CanAccessScopeAsync(request.ClientId, request.SiteId, ActionType.Create))
            return Forbid();

        try
        {
            return Ok(await fileService.CompleteUploadAsync(request, CurrentUser, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("upload/abort")]
    [RequirePermission(ResourceType.Files, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> AbortUpload(
        [FromQuery] string objectKey,
        [FromQuery] string uploadId,
        CancellationToken ct)
    {
        await fileService.AbortUploadAsync(objectKey, uploadId, ct);
        return NoContent();
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission(ResourceType.Files, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateManagedFileRequest request, CancellationToken ct)
    {
        var existing = await fileService.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Arquivo não encontrado." });
        if (!await CanAccessFileAsync(existing, ActionType.Edit))
            return Forbid();

        try
        {
            return Ok(await fileService.UpdateAsync(id, request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(ResourceType.Files, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var existing = await fileService.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Arquivo não encontrado." });
        if (!await CanAccessFileAsync(existing, ActionType.Delete))
            return Forbid();

        await fileService.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/download-url")]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> GetDownloadUrl(Guid id, CancellationToken ct)
    {
        var existing = await fileService.GetByIdAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Arquivo não encontrado." });
        if (!await CanAccessFileAsync(existing, ActionType.View))
            return Forbid();

        return Ok(await fileService.GetAdminDownloadUrlAsync(id, ct));
    }

    // ── Pastas (Folders) ──

    [HttpGet("folders")]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> ListFolders(
        [FromQuery] string? parentPath,
        [FromQuery] bool includeAll = false,
        [FromQuery] Core.Enums.FileScope? scope = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Core.Enums.FileVisibility? visibility = null,
        CancellationToken ct = default)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.View);
        var (allowedClients, allowedSites) = access.HasGlobalAccess
            ? (null, null)
            : (access.AllowedClientIds, access.AllowedSiteIds);

        var folders = await fileService.ListFoldersAsync(
            parentPath, includeAll, scope, clientId, siteId, visibility, allowedClients, allowedSites, ct);
        return Ok(folders);
    }

    [HttpPost("folders")]
    [RequirePermission(ResourceType.Files, ActionType.Create, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateFolder([FromBody] CreateManagedFileFolderRequest request, CancellationToken ct)
    {
        if (!await CanAccessScopeAsync(request.ClientId, request.SiteId, ActionType.Create))
            return Forbid();

        try
        {
            return Ok(await fileService.CreateFolderAsync(request, CurrentUser, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPatch("folders/{id:guid}")]
    [RequirePermission(ResourceType.Files, ActionType.Edit, ScopeSource.AccessList)]
    public async Task<IActionResult> UpdateFolder(Guid id, [FromBody] UpdateManagedFileFolderRequest request, CancellationToken ct)
    {
        var existing = await fileService.GetFolderByIdAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Pasta não encontrada." });
        if (!await CanAccessFolderAsync(existing, ActionType.Edit))
            return Forbid();

        try
        {
            return Ok(await fileService.UpdateFolderAsync(id, request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("folders/{id:guid}")]
    [RequirePermission(ResourceType.Files, ActionType.Delete, ScopeSource.AccessList)]
    public async Task<IActionResult> DeleteFolder(Guid id, [FromQuery] bool moveFilesToParent = true, CancellationToken ct = default)
    {
        var existing = await fileService.GetFolderByIdAsync(id, ct);
        if (existing is null)
            return NotFound(new { error = "Pasta não encontrada." });
        if (!await CanAccessFolderAsync(existing, ActionType.Delete))
            return Forbid();

        try
        {
            await fileService.DeleteFolderAsync(id, moveFilesToParent, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── Share Links (Download) ──

    [HttpGet("share-links")]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> ListShareLinks([FromQuery] Guid? clientId, [FromQuery] Guid? siteId, CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.View);
        var links = await linkService.ListShareLinksAsync(clientId, siteId, ct);
        if (access.HasGlobalAccess)
            return Ok(links);

        return Ok(links.Where(l => TenantScopePolicy.Allows(access, l.ClientId ?? Guid.Empty, l.SiteId ?? Guid.Empty)).ToArray());
    }

    [HttpPost("share-links")]
    [RequirePermission(ResourceType.Files, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateShareLink([FromBody] CreateShareLinkRequest request, CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.Execute);
        if (request.ClientId.HasValue || request.SiteId.HasValue)
        {
            if (!await CanAccessScopeAsync(request.ClientId, request.SiteId, ActionType.Execute))
                return Forbid();
        }
        else if (!access.HasGlobalAccess)
        {
            return Forbid();
        }

        if (!access.HasGlobalAccess && request.FileIds is { Count: > 0 })
        {
            foreach (var fileId in request.FileIds.Distinct())
            {
                var file = await fileService.GetByIdAsync(fileId, ct);
                if (file is not null && !await CanAccessFileAsync(file, ActionType.View))
                    return Forbid();
            }
        }

        try
        {
            return Ok(await linkService.CreateShareLinkAsync(request, CurrentUser, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("share-links/{id:guid}")]
    [RequirePermission(ResourceType.Files, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> RevokeShareLink(Guid id, CancellationToken ct)
    {
        var scope = await linkService.GetShareLinkScopeAsync(id, ct);
        if (scope is null)
            return NotFound(new { error = "Link não encontrado." });

        if (scope.Value.ClientId.HasValue || scope.Value.SiteId.HasValue)
        {
            if (!await CanAccessScopeAsync(scope.Value.ClientId, scope.Value.SiteId, ActionType.Execute))
                return Forbid();
        }
        else if (!(await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.Execute)).HasGlobalAccess)
        {
            return Forbid();
        }

        try
        {
            await linkService.RevokeShareLinkAsync(id, CurrentUser, ct);
            return NoContent();
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Link não encontrado." });
        }
    }

    // ── Upload Links (File Drop) ──

    [HttpGet("upload-links")]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.AccessList)]
    public async Task<IActionResult> ListUploadLinks([FromQuery] Guid? clientId, [FromQuery] Guid? siteId, CancellationToken ct)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.View);
        var links = await linkService.ListUploadLinksAsync(clientId, siteId, ct: ct);
        if (access.HasGlobalAccess)
            return Ok(links);

        return Ok(links.Where(l => TenantScopePolicy.Allows(access, l.ClientId ?? Guid.Empty, l.SiteId ?? Guid.Empty)).ToArray());
    }

    [HttpPost("upload-links")]
    [RequirePermission(ResourceType.Files, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> CreateUploadLink([FromBody] CreateUploadLinkRequest request, CancellationToken ct)
    {
        if (request.ClientId.HasValue || request.SiteId.HasValue)
        {
            if (!await CanAccessScopeAsync(request.ClientId, request.SiteId, ActionType.Execute))
                return Forbid();
        }
        else if (!(await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.Execute)).HasGlobalAccess)
        {
            return Forbid();
        }

        try
        {
            return Ok(await linkService.CreateUploadLinkAsync(request, CurrentUser, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("upload-links/{id:guid}")]
    [RequirePermission(ResourceType.Files, ActionType.Execute, ScopeSource.AccessList)]
    public async Task<IActionResult> RevokeUploadLink(Guid id, CancellationToken ct)
    {
        var scope = await linkService.GetUploadLinkScopeAsync(id, ct);
        if (scope is null)
            return NotFound(new { error = "Link não encontrado." });

        if (scope.Value.ClientId.HasValue || scope.Value.SiteId.HasValue)
        {
            if (!await CanAccessScopeAsync(scope.Value.ClientId, scope.Value.SiteId, ActionType.Execute))
                return Forbid();
        }
        else if (!(await scopeContext.GetAccessAsync(ResourceType.Files, ActionType.Execute)).HasGlobalAccess)
        {
            return Forbid();
        }

        try
        {
            await linkService.RevokeUploadLinkAsync(id, CurrentUser, ct);
            return NoContent();
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { error = "Link não encontrado." });
        }
    }

    [HttpGet("logs")]
    [RequirePermission(ResourceType.Files, ActionType.View, ScopeSource.Global)]
    public async Task<IActionResult> ListLogs([FromQuery] string? eventType, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await fileService.ListAccessLogsAsync(eventType, limit, ct));

    private async Task<bool> CanAccessFileAsync(ManagedFileDto file, ActionType action)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, action);
        if (access.HasGlobalAccess)
            return true;
        if (file.Scope == Core.Enums.FileScope.Global)
            return action == ActionType.View;
        return TenantScopePolicy.Allows(access, file.ClientId ?? Guid.Empty, file.SiteId ?? Guid.Empty);
    }

    private async Task<bool> CanAccessFolderAsync(ManagedFileFolderDto folder, ActionType action)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, action);
        if (access.HasGlobalAccess)
            return true;
        if (folder.Scope == Core.Enums.FileScope.Global)
            return action == ActionType.View;
        return TenantScopePolicy.Allows(access, folder.ClientId ?? Guid.Empty, folder.SiteId ?? Guid.Empty);
    }

    private async Task<bool> CanAccessScopeAsync(Guid? clientId, Guid? siteId, ActionType action)
    {
        var access = await scopeContext.GetAccessAsync(ResourceType.Files, action);
        if (access.HasGlobalAccess)
            return true;
        if (clientId is null && siteId is null)
            return false;
        if (siteId.HasValue && !clientId.HasValue)
        {
            var site = await siteRepo.GetByIdAsync(siteId.Value);
            clientId = site?.ClientId;
        }
        return TenantScopePolicy.Allows(access, clientId ?? Guid.Empty, siteId ?? Guid.Empty);
    }
}
