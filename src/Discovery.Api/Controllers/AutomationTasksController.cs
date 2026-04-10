using Discovery.Core.DTOs;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/automation/tasks")]
public class AutomationTasksController : ControllerBase
{
    private readonly IAutomationTaskService _service;

    public AutomationTasksController(IAutomationTaskService service)
    {
        _service = service;
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
        var item = await _service.GetByIdAsync(id, includeInactive, cancellationToken);
        if (item is null)
            return NotFound();

        return Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateAutomationTaskRequest request,
        CancellationToken cancellationToken = default)
    {
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

            if (updated is null)
                return NotFound();

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
        var correlationId = GetOrCreateCorrelationId();
        var restored = await _service.RestoreAsync(
            id,
            HttpContext.Items["Username"] as string ?? "api",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            correlationId,
            reason,
            cancellationToken);

        if (restored is null)
            return NotFound();

        Response.Headers["X-Correlation-Id"] = correlationId;
        return Ok(restored);
    }

    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> GetAudit(
        Guid id,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
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
        var preview = await _service.PreviewTargetAgentsAsync(id, limit, offset, cancellationToken);
        if (preview is null)
            return NotFound();

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
}
