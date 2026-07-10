using Discovery.Core.Cqrs.EscalationRules.Commands;
using Discovery.Core.Cqrs.EscalationRules.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/escalation-rules")]
public class EscalationRulesController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? workflowProfileId = null)
    {
        var result = await mediator.Send(new ListEscalationRulesQuery(workflowProfileId));
        return result.ToActionResult();
    }

    /// <summary>
    /// Lista regras de escalation pelo perfil de workflow.
    /// Rota semântica alternativa ao query parameter ?workflowProfileId=.
    /// </summary>
    [HttpGet("by-profile/{profileId:guid}")]
    public async Task<IActionResult> GetByProfile(Guid profileId)
    {
        var result = await mediator.Send(new ListEscalationRulesQuery(profileId));
        return result.ToActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new GetEscalationRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEscalationRuleCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEscalationRuleCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new DeleteEscalationRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }
}
