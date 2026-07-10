using Discovery.Core.Cqrs.Reports.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/reports")]
public class ReportsController(IMediator mediator) : ControllerBase
{
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
}
