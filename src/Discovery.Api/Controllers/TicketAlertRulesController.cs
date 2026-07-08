using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/ticket-alert-rules")]
public class TicketAlertRulesController(ITicketAlertRuleRepository repo) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var rules = await repo.GetAllAsync();
        return Ok(rules);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var rule = await repo.GetByIdAsync(id);
        return rule is null ? NotFound() : Ok(rule);
    }

    [HttpGet("by-workflow-state/{workflowStateId:guid}")]
    public async Task<IActionResult> GetByWorkflowState(Guid workflowStateId)
    {
        var rules = await repo.GetByWorkflowStateIdAsync(workflowStateId);
        return Ok(rules);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTicketAlertRuleRequest request)
    {
        var entity = new TicketAlertRule
        {
            WorkflowStateId = request.WorkflowStateId,
            Title = request.Title,
            Message = request.Message,
            AlertType = request.AlertType,
            TimeoutSeconds = request.TimeoutSeconds,
            ActionsJson = request.ActionsJson,
            DefaultAction = request.DefaultAction,
            Icon = request.Icon,
            ScopePreference = request.ScopePreference,
            IsEnabled = request.IsEnabled
        };
        var created = await repo.CreateAsync(entity);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTicketAlertRuleRequest request)
    {
        var existing = await repo.GetByIdAsync(id);
        if (existing is null) return NotFound();
        existing.WorkflowStateId = request.WorkflowStateId;
        existing.Title = request.Title;
        existing.Message = request.Message;
        existing.AlertType = request.AlertType;
        existing.TimeoutSeconds = request.TimeoutSeconds;
        existing.ActionsJson = request.ActionsJson;
        existing.DefaultAction = request.DefaultAction;
        existing.Icon = request.Icon;
        existing.ScopePreference = request.ScopePreference;
        existing.IsEnabled = request.IsEnabled;
        var updated = await repo.UpdateAsync(existing);
        return Ok(updated);
    }

    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id)
    {
        var existing = await repo.GetByIdAsync(id);
        if (existing is null) return NotFound();
        existing.IsEnabled = !existing.IsEnabled;
        var updated = await repo.UpdateAsync(existing);
        return Ok(updated);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await repo.DeleteAsync(id);
        return NoContent();
    }
}

public record CreateTicketAlertRuleRequest(
    Guid WorkflowStateId,
    string Title,
    string Message,
    Discovery.Core.Enums.PsadtAlertType AlertType = Discovery.Core.Enums.PsadtAlertType.Toast,
    int? TimeoutSeconds = 15,
    string? ActionsJson = null,
    string? DefaultAction = null,
    string Icon = "info",
    Discovery.Core.Enums.AlertScopeType ScopePreference = Discovery.Core.Enums.AlertScopeType.Agent,
    bool IsEnabled = true
);

public record UpdateTicketAlertRuleRequest(
    Guid WorkflowStateId,
    string Title,
    string Message,
    Discovery.Core.Enums.PsadtAlertType AlertType,
    int? TimeoutSeconds,
    string? ActionsJson,
    string? DefaultAction,
    string Icon,
    Discovery.Core.Enums.AlertScopeType ScopePreference,
    bool IsEnabled
);
