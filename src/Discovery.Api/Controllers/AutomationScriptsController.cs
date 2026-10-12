using Discovery.Core.Cqrs.AutomationScripts.Commands;
using Discovery.Core.Cqrs.CustomFields.Queries;
using Discovery.Core.Cqrs.AutomationScripts.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/automation/scripts")]
public class AutomationScriptsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? clientId = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new ListAutomationScriptsQuery(clientId, cursor, limit));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetAutomationScriptByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAutomationScriptCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    /// <summary>Campos customizados vinculados a este script (visão reversa).</summary>
    [HttpGet("{id:guid}/custom-fields")]
    public async Task<IActionResult> GetCustomFields(Guid id)
    {
        var result = await mediator.Send(new ListCustomFieldsQuery(null, false, null, id));
        return result.ToActionResult();
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutomationScriptCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string? reason = null)
    {
        var result = await mediator.Send(new DeleteAutomationScriptCommand(id, reason));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>
    /// Trilha de auditoria do script (quem mudou o quê e quando). Consumida pelo
    /// modal "Auditoria do script" e pela página /automation/audit.
    /// </summary>
    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> GetAudit(Guid id, [FromQuery] int limit = 50)
    {
        var result = await mediator.Send(new GetAutomationScriptAuditQuery(id, Math.Clamp(limit, 1, 200)));
        return result.ToActionResult();
    }

    /// <summary>
    /// Payload de consumo do script: conteúdo + hash SHA-256 — o mesmo material que
    /// o agent recebe — e registra um evento "Consumed" na auditoria. Usado para
    /// conferir/exportar o script sem executá-lo.
    /// </summary>
    [HttpGet("{id:guid}/consume")]
    public async Task<IActionResult> Consume(Guid id)
    {
        var result = await mediator.Send(new GetAutomationScriptConsumeQuery(id, CurrentActor(), ClientIp(), CorrelationId()));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Ator da ação para auditoria (mesma convenção de AgentLabelsController).</summary>
    private string CurrentActor()
        => HttpContext.Items["UserId"] is Guid userId
            ? $"user:{userId}"
            : HttpContext.Items["Username"] as string ?? "system";

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string CorrelationId()
        => Request.Headers.TryGetValue("X-Correlation-Id", out var values) && !string.IsNullOrWhiteSpace(values.ToString())
            ? values.ToString()
            : "api";
}
