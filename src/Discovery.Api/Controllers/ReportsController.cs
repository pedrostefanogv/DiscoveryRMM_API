using Discovery.Core.Cqrs.Reports.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/reports")]
public class ReportsController(IMediator mediator) : ControllerBase
{
    // --- Executions ---
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new ListReportsQuery(clientId));
        return result.ToActionResult();
    }

    [HttpGet("{executionId:guid}")]
    public async Task<IActionResult> GetById(Guid executionId, [FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new GetReportExecutionQuery(executionId, clientId));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // --- Templates ---
    [HttpGet("templates")]
    public async Task<IActionResult> ListTemplates([FromQuery] Guid? clientId = null, [FromQuery] bool? isActive = true)
    {
        var result = await mediator.Send(new ListReportTemplatesQuery(clientId, isActive));
        return result.ToActionResult();
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> GetTemplateById(Guid id, [FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new GetReportTemplateByIdQuery(id, clientId));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("templates")]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateReportTemplateCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => CreatedAtAction(nameof(GetTemplateById), new { id = dto.Id }, dto),
            failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("templates/{id:guid}")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] UpdateReportTemplateCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("templates/{id:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id, [FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new DeleteReportTemplateCommand(id, clientId));
        return result.Match<IActionResult>(
            success: _ => NoContent(),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    // --- Run ---
    [HttpGet("executions")]
    public async Task<IActionResult> ListExecutions([FromQuery] Guid? clientId = null)
    {
        var result = await mediator.Send(new ListReportsQuery(clientId));
        return result.ToActionResult();
    }

    [HttpPost("run")]
    public async Task<IActionResult> RunNow([FromBody] RunReportNowCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(
            success: dto => AcceptedAtAction(nameof(GetById), new { executionId = dto.Id }, dto),
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }
}
