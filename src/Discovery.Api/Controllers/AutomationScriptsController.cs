using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces.Auth;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/automation/scripts")]
public class AutomationScriptsController : ControllerBase
{
    private readonly IAutomationScriptService _service;
    private readonly IPermissionService _permissionService;

    public AutomationScriptsController(IAutomationScriptService service, IPermissionService permissionService)
    {
        _service = service;
        _permissionService = permissionService;
    }

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool activeOnly = true,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        if (!await HasAutomationPermissionAsync(userId, ActionType.View, clientId))
            return Forbid();

        var page = await _service.GetListAsync(clientId, activeOnly, limit, offset, cancellationToken);
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

        if (!await HasAutomationPermissionAsync(userId, ActionType.View, item.ClientId))
            return Forbid();

        return Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateAutomationScriptRequest request,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        if (!await HasAutomationPermissionAsync(userId, ActionType.Create, request.ClientId))
            return Forbid();

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
        [FromBody] UpdateAutomationScriptRequest request,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var existing = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (existing is null)
            return NotFound();

        if (!await HasAutomationPermissionAsync(userId, ActionType.Edit, existing.ClientId))
            return Forbid();

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

        var existing = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (existing is null)
            return NotFound();

        if (!await HasAutomationPermissionAsync(userId, ActionType.Delete, existing.ClientId))
            return Forbid();

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

    [HttpGet("{id:guid}/consume")]
    public async Task<IActionResult> Consume(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var script = await _service.GetByIdAsync(id, includeInactive: false, cancellationToken);
        if (script is null)
            return NotFound();

        if (!await HasAutomationPermissionAsync(userId, ActionType.Execute, script.ClientId))
            return Forbid();

        var correlationId = GetOrCreateCorrelationId();
        var payload = await _service.GetConsumePayloadAsync(
            id,
            HttpContext.Items["Username"] as string ?? "agent",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            correlationId,
            cancellationToken);

        if (payload is null)
            return NotFound();

        Response.Headers["X-Correlation-Id"] = correlationId;
        return Ok(payload);
    }

    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> GetAudit(
        Guid id,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["UserId"] is not Guid userId)
            return Unauthorized(new { error = "User not authenticated." });

        var script = await _service.GetByIdAsync(id, includeInactive: true, cancellationToken);
        if (script is null)
            return NotFound();

        if (!await HasAutomationPermissionAsync(userId, ActionType.View, script.ClientId))
            return Forbid();

        var entries = await _service.GetAuditAsync(id, limit, cancellationToken);
        return Ok(new
        {
            scriptId = id,
            count = entries.Count,
            items = entries
        });
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

    private Task<bool> HasAutomationPermissionAsync(Guid userId, ActionType action, Guid? clientId)
    {
        if (clientId.HasValue)
            return _permissionService.HasPermissionAsync(
                userId,
                ResourceType.Automation,
                action,
                ScopeLevel.Client,
                clientId.Value);

        return _permissionService.HasPermissionAsync(
            userId,
            ResourceType.Automation,
            action,
            ScopeLevel.Global);
    }
}
