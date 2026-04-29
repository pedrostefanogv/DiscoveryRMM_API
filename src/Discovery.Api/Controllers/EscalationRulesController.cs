using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/escalation-rules")]
public class EscalationRulesController : ControllerBase
{
    private readonly ITicketEscalationRuleRepository _repo;

    public EscalationRulesController(ITicketEscalationRuleRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _repo.GetAllActiveAsync());

    [HttpGet("by-profile/{workflowProfileId:guid}")]
    public async Task<IActionResult> GetByProfile(Guid workflowProfileId) =>
        Ok(await _repo.GetByWorkflowProfileIdAsync(workflowProfileId));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var rule = await _repo.GetByIdAsync(id);
        return rule is null ? NotFound() : Ok(rule);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEscalationRuleRequest request)
    {
        var rule = new TicketEscalationRule
        {
            WorkflowProfileId = request.WorkflowProfileId,
            Name = request.Name,
            TriggerAtSlaPercent = request.TriggerAtSlaPercent,
            TriggerAtHoursBefore = request.TriggerAtHoursBefore,
            ReassignToUserId = request.ReassignToUserId,
            ReassignToDepartmentId = request.ReassignToDepartmentId,
            BumpPriority = request.BumpPriority,
            NotifyAssignee = request.NotifyAssignee,
            IsActive = true
        };

        var created = await _repo.CreateAsync(rule);
        return Created($"api/escalation-rules/{created.Id}", created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateEscalationRuleRequest request)
    {
        var rule = await _repo.GetByIdAsync(id);
        if (rule is null) return NotFound();

        rule.Name = request.Name;
        rule.TriggerAtSlaPercent = request.TriggerAtSlaPercent;
        rule.TriggerAtHoursBefore = request.TriggerAtHoursBefore;
        rule.ReassignToUserId = request.ReassignToUserId;
        rule.ReassignToDepartmentId = request.ReassignToDepartmentId;
        rule.BumpPriority = request.BumpPriority;
        rule.NotifyAssignee = request.NotifyAssignee;
        rule.IsActive = request.IsActive;

        await _repo.UpdateAsync(rule);
        return Ok(rule);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var rule = await _repo.GetByIdAsync(id);
        if (rule is null) return NotFound();
        await _repo.DeleteAsync(id);
        return NoContent();
    }
}

public record CreateEscalationRuleRequest(
    Guid WorkflowProfileId,
    string Name,
    int TriggerAtSlaPercent = 0,
    int TriggerAtHoursBefore = 0,
    Guid? ReassignToUserId = null,
    Guid? ReassignToDepartmentId = null,
    bool BumpPriority = false,
    bool NotifyAssignee = true
);

public record UpdateEscalationRuleRequest(
    string Name,
    int TriggerAtSlaPercent = 0,
    int TriggerAtHoursBefore = 0,
    Guid? ReassignToUserId = null,
    Guid? ReassignToDepartmentId = null,
    bool BumpPriority = false,
    bool NotifyAssignee = true,
    bool IsActive = true
);
