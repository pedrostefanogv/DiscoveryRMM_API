using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/automation/tasks")]
public class AutomationTasksController : ControllerBase
{
    private readonly IAutomationTaskService _service;
    private readonly IPermissionService _permissionService;
    private readonly ISiteRepository _siteRepository;
    private readonly IAgentRepository _agentRepository;

    public AutomationTasksController(
        IAutomationTaskService service,
        IPermissionService permissionService,
        ISiteRepository siteRepository,
        IAgentRepository agentRepository)
    {
        _service = service;
        _permissionService = permissionService;
        _siteRepository = siteRepository;
        _agentRepository = agentRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] AppApprovalScopeType? scopeType = null,
        [FromQuery] Guid? scopeId = null,
        [FromQuery] bool activeOnly = true,
        [FromQuery] bool deletedOnly = false,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] string? search = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] Guid? siteId = null,
        [FromQuery] Guid? agentId = null,
        [FromQuery] List<AppApprovalScopeType>? scopeTypes = null,
        [FromQuery] List<AutomationTaskActionType>? actionTypes = null,
        [FromQuery] List<string>? labels = null,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var permissionAction = ActionType.View;
        var permissionCheck = await ResolveAndCheckPermissionAsync(
            userId,
            permissionAction,
            scopeType,
            scopeId,
            clientId,
            siteId,
            agentId,
            cancellationToken);

        if (permissionCheck is not null)
            return permissionCheck;

        var page = await _service.GetListAsync(
            scopeType,
            scopeId,
            activeOnly,
            deletedOnly,
            includeDeleted,
            search,
            clientId,
            siteId,
            agentId,
            scopeTypes,
            actionTypes,
            labels,
            limit,
            offset,
            cancellationToken);
        return Ok(page);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var item = await _service.GetByIdAsync(id, includeInactive, cancellationToken);
        if (item is null)
            return NotFound();

        var permissionCheck = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.View,
            item.ScopeType,
            item.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (permissionCheck is not null)
            return permissionCheck;

        return Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateAutomationTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var permissionCheck = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.Create,
            request.ScopeType,
            request.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (permissionCheck is not null)
            return permissionCheck;

        try
        {
            var correlationId = GetOrCreateCorrelationId();
            var created = await _service.CreateAsync(
                request,
                HttpContext.Items["Username"] as string ?? "api",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                correlationId,
                cancellationToken);

            Response.Headers["X-Correlation-Id"] = correlationId;
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateAutomationTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var current = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (current is null)
            return NotFound();

        var currentPermission = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.Edit,
            current.ScopeType,
            current.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (currentPermission is not null)
            return currentPermission;

        var targetPermission = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.Edit,
            request.ScopeType,
            request.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (targetPermission is not null)
            return targetPermission;

        try
        {
            var correlationId = GetOrCreateCorrelationId();
            var updated = await _service.UpdateAsync(
                id,
                request,
                HttpContext.Items["Username"] as string ?? "api",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                correlationId,
                cancellationToken);

            Response.Headers["X-Correlation-Id"] = correlationId;
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var current = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (current is null)
            return NotFound();

        var permissionCheck = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.Delete,
            current.ScopeType,
            current.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (permissionCheck is not null)
            return permissionCheck;

        var correlationId = GetOrCreateCorrelationId();
        var deleted = await _service.DeleteAsync(
            id,
            HttpContext.Items["Username"] as string ?? "api",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            correlationId,
            reason,
            cancellationToken);

        if (!deleted)
            return NotFound();

        Response.Headers["X-Correlation-Id"] = correlationId;
        return NoContent();
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(
        Guid id,
        [FromQuery] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var current = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (current is null)
            return NotFound();

        var permissionCheck = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.Edit,
            current.ScopeType,
            current.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (permissionCheck is not null)
            return permissionCheck;

        var correlationId = GetOrCreateCorrelationId();
        var restored = await _service.RestoreAsync(
            id,
            HttpContext.Items["Username"] as string ?? "api",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            correlationId,
            reason,
            cancellationToken);

        Response.Headers["X-Correlation-Id"] = correlationId;
        return Ok(restored);
    }

    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> GetAudit(
        Guid id,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var task = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (task is null)
            return NotFound();

        var permissionCheck = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.View,
            task.ScopeType,
            task.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (permissionCheck is not null)
            return permissionCheck;

        var entries = await _service.GetAuditAsync(id, limit, cancellationToken);
        return Ok(new
        {
            taskId = id,
            count = entries.Count,
            items = entries
        });
    }

    [HttpGet("{id:guid}/preview-agents")]
    public async Task<IActionResult> PreviewTargetAgents(
        Guid id,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var task = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (task is null)
            return NotFound();

        var permissionCheck = await ResolveAndCheckPermissionAsync(
            userId,
            ActionType.View,
            task.ScopeType,
            task.ScopeId,
            null,
            null,
            null,
            cancellationToken);

        if (permissionCheck is not null)
            return permissionCheck;

        var preview = await _service.PreviewTargetAgentsAsync(id, limit, offset, cancellationToken);
        return Ok(preview);
    }

    private string GetOrCreateCorrelationId()
    {
        if (Request.Headers.TryGetValue("X-Correlation-Id", out var values))
        {
            var existing = values.ToString();
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        return Guid.NewGuid().ToString("N");
    }

    private async Task<IActionResult?> ResolveAndCheckPermissionAsync(
        Guid userId,
        ActionType action,
        AppApprovalScopeType? explicitScopeType,
        Guid? explicitScopeId,
        Guid? clientId,
        Guid? siteId,
        Guid? agentId,
        CancellationToken cancellationToken)
    {
        ScopeLevel scopeLevel;
        Guid? scopeId;
        Guid? parentScopeId;

        if (explicitScopeType.HasValue)
        {
            var resolved = await ResolveScopeAsync(explicitScopeType.Value, explicitScopeId, cancellationToken);
            if (resolved.error is not null)
                return resolved.error;

            scopeLevel = resolved.scopeLevel;
            scopeId = resolved.scopeId;
            parentScopeId = resolved.parentScopeId;
        }
        else if (agentId.HasValue)
        {
            var resolved = await ResolveScopeAsync(AppApprovalScopeType.Agent, agentId, cancellationToken);
            if (resolved.error is not null)
                return resolved.error;

            scopeLevel = resolved.scopeLevel;
            scopeId = resolved.scopeId;
            parentScopeId = resolved.parentScopeId;
        }
        else if (siteId.HasValue)
        {
            var resolved = await ResolveScopeAsync(AppApprovalScopeType.Site, siteId, cancellationToken);
            if (resolved.error is not null)
                return resolved.error;

            scopeLevel = resolved.scopeLevel;
            scopeId = resolved.scopeId;
            parentScopeId = resolved.parentScopeId;
        }
        else if (clientId.HasValue)
        {
            scopeLevel = ScopeLevel.Client;
            scopeId = clientId;
            parentScopeId = null;
        }
        else
        {
            scopeLevel = ScopeLevel.Global;
            scopeId = null;
            parentScopeId = null;
        }

        var allowed = await _permissionService.HasPermissionAsync(
            userId,
            ResourceType.Automation,
            action,
            scopeLevel,
            scopeId,
            parentScopeId);

        return allowed ? null : Forbid();
    }

    private async Task<(ScopeLevel scopeLevel, Guid? scopeId, Guid? parentScopeId, IActionResult? error)> ResolveScopeAsync(
        AppApprovalScopeType scopeType,
        Guid? rawScopeId,
        CancellationToken cancellationToken)
    {
        switch (scopeType)
        {
            case AppApprovalScopeType.Global:
                return (ScopeLevel.Global, null, null, null);

            case AppApprovalScopeType.Client:
                if (!rawScopeId.HasValue)
                    return (default, null, null, BadRequest(new { error = "ScopeId is required for client scope." }));
                return (ScopeLevel.Client, rawScopeId.Value, null, null);

            case AppApprovalScopeType.Site:
                if (!rawScopeId.HasValue)
                    return (default, null, null, BadRequest(new { error = "ScopeId is required for site scope." }));

                var site = await _siteRepository.GetByIdAsync(rawScopeId.Value);
                if (site is null)
                    return (default, null, null, NotFound(new { error = "Site not found." }));

                return (ScopeLevel.Site, site.Id, site.ClientId, null);

            case AppApprovalScopeType.Agent:
                if (!rawScopeId.HasValue)
                    return (default, null, null, BadRequest(new { error = "ScopeId is required for agent scope." }));

                var agent = await _agentRepository.GetByIdAsync(rawScopeId.Value);
                if (agent is null)
                    return (default, null, null, NotFound(new { error = "Agent not found." }));

                var agentSite = await _siteRepository.GetByIdAsync(agent.SiteId);
                if (agentSite is null)
                    return (default, null, null, NotFound(new { error = "Site not found for agent." }));

                return (ScopeLevel.Site, agentSite.Id, agentSite.ClientId, null);

            default:
                return (default, null, null, BadRequest(new { error = "Unsupported scope type." }));
        }
    }
}
