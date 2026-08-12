using Discovery.Api.Filters;
using Discovery.Core.Cqrs.AgentLabels.Commands;
using Discovery.Core.Cqrs.AgentLabels.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Enums.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

using Discovery.Api;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-labels")]
public class AgentLabelsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetByAgent([FromQuery] Guid agentId)
    {
        var result = await mediator.Send(new ListAgentLabelsQuery(agentId));
        return result.ToActionResult();
    }

    [HttpGet("agents/{agentId:guid}")]
    public async Task<IActionResult> GetByAgentId(Guid agentId)
    {
        var result = await mediator.Send(new ListAgentLabelsQuery(agentId));
        return result.ToActionResult();
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddAgentLabelCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => errors[0].Code == "Conflict" ? Conflict(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPost("manual")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> AddManual([FromBody] AddAgentLabelCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => Created("", dto), failure: errors => errors[0].Code == "Conflict" ? Conflict(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id)
    {
        var result = await mediator.Send(new RemoveAgentLabelCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpDelete("manual/{id:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.Edit)]
    public async Task<IActionResult> RemoveManual(Guid id)
    {
        var result = await mediator.Send(new RemoveAgentLabelCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("reprocess")]
    [RequirePermission(ResourceType.Agents, ActionType.Execute)]
    public async Task<IActionResult> Reprocess()
    {
        var result = await mediator.Send(new ReprocessLabelsCommand());
        return result.Match<IActionResult>(success: _ => Ok(new { message = "Reprocessamento iniciado." }), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpGet("distinct")]
    public async Task<IActionResult> GetDistinct()
    {
        var result = await mediator.Send(new GetDistinctLabelsQuery());
        return result.ToActionResult();
    }

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules([FromQuery] bool includeDisabled = true)
    {
        var result = await mediator.Send(new ListLabelRulesQuery(includeDisabled));
        return result.ToActionResult();
    }

    [HttpGet("rules/{id:guid}")]
    public async Task<IActionResult> GetRuleById(Guid id)
    {
        var result = await mediator.Send(new GetLabelRuleByIdQuery(id));
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule([FromBody] CreateLabelRuleCommand cmd)
    {
        var result = await mediator.Send(cmd);
        return result.Match<IActionResult>(success: dto => CreatedAtAction(nameof(GetRuleById), new { id = dto.Id }, dto), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] UpdateLabelRuleCommand cmd)
    {
        var result = await mediator.Send(cmd with { Id = id });
        return result.Match<IActionResult>(success: Ok, failure: errors => errors[0].Code == "NotFound" ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) }) : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }

    [HttpDelete("rules/{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id)
    {
        var result = await mediator.Send(new DeleteLabelRuleCommand(id));
        return result.Match<IActionResult>(success: _ => NoContent(), failure: errors => BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Lists available Agent-scoped custom fields usable in label rule expressions.</summary>
    [HttpGet("rules/available-custom-fields")]
    public async Task<IActionResult> GetAvailableCustomFields()
    {
        var result = await mediator.Send(new GetAvailableCustomFieldsQuery());
        return result.ToActionResult();
    }

    /// <summary>Lists agents currently matched by a label rule.</summary>
    [HttpGet("rules/{id:guid}/agents")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetAgentsByRule(Guid id)
    {
        var result = await mediator.Send(new ListAgentsByRuleQuery(id));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message }) }));
    }

    /// <summary>Runs a dry-run (preview) of a label rule expression against a single agent.</summary>
    [HttpPost("rules/dry-run")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> DryRun([FromBody] AgentLabelRuleDryRunRequest request)
    {
        var result = await mediator.Send(new DryRunLabelRuleQuery(request));
        return result.Match<IActionResult>(
            success: Ok,
            failure: errors => errors[0].Code == "NotFound"
                ? NotFound(new { errors = errors.Select(e => new { e.Code, e.Message }) })
                : BadRequest(new { errors = errors.Select(e => new { e.Code, e.Message, e.Field }) }));
    }
}
