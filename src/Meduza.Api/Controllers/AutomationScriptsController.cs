using Meduza.Core.DTOs;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/automation/scripts")]
public class AutomationScriptsController : ControllerBase
{
    private readonly IAutomationScriptService _service;

    public AutomationScriptsController(IAutomationScriptService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] Guid? clientId = null,
        [FromQuery] bool activeOnly = true,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var page = await _service.GetListAsync(clientId, activeOnly, limit, offset, cancellationToken);
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
        [FromBody] CreateAutomationScriptRequest request,
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
        [FromBody] UpdateAutomationScriptRequest request,
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

    [HttpGet("{id:guid}/consume")]
    public async Task<IActionResult> Consume(
        Guid id,
        CancellationToken cancellationToken = default)
    {
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
}
