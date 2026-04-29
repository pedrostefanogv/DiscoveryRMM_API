using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

/// <summary>
/// CRUD para regras de alerta automático vinculadas a estados de workflow.
/// Quando um ticket transita para o WorkflowStateId configurado,
/// o servidor dispara automaticamente o alerta PSADT.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/ticket-alert-rules")]
public class TicketAlertRulesController : ControllerBase
{
    private readonly ITicketAlertRuleRepository _repo;

    public TicketAlertRulesController(ITicketAlertRuleRepository repo)
        => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var rules = await _repo.GetAllAsync();
        return Ok(rules);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var rule = await _repo.GetByIdAsync(id);
        return rule is null ? NotFound() : Ok(rule);
    }

    [HttpGet("by-workflow-state/{workflowStateId:guid}")]
    public async Task<IActionResult> GetByWorkflowState(Guid workflowStateId)
    {
        var rules = await _repo.GetByWorkflowStateIdAsync(workflowStateId);
        return Ok(rules);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertTicketAlertRuleRequest request)
    {
        if (!ValidateRequest(request, out var error))
            return BadRequest(new { error });

        var rule = new TicketAlertRule
        {
            WorkflowStateId = request.WorkflowStateId,
            Title = request.Title,
            Message = request.Message,
            AlertType = request.AlertType,
            TimeoutSeconds = ResolveTimeout(request),
            ActionsJson = request.ActionsJson,
            DefaultAction = request.DefaultAction,
            Icon = request.Icon ?? "info",
            ScopePreference = request.ScopePreference,
            IsEnabled = request.IsEnabled ?? true
        };

        var created = await _repo.CreateAsync(rule);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertTicketAlertRuleRequest request)
    {
        if (!ValidateRequest(request, out var error))
            return BadRequest(new { error });

        var rule = await _repo.GetByIdAsync(id);
        if (rule is null) return NotFound();

        rule.WorkflowStateId = request.WorkflowStateId;
        rule.Title = request.Title;
        rule.Message = request.Message;
        rule.AlertType = request.AlertType;
        rule.TimeoutSeconds = ResolveTimeout(request);
        rule.ActionsJson = request.ActionsJson;
        rule.DefaultAction = request.DefaultAction;
        rule.Icon = request.Icon ?? "info";
        rule.ScopePreference = request.ScopePreference;
        rule.IsEnabled = request.IsEnabled ?? rule.IsEnabled;

        var updated = await _repo.UpdateAsync(rule);
        return Ok(updated);
    }

    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id)
    {
        var rule = await _repo.GetByIdAsync(id);
        if (rule is null) return NotFound();

        rule.IsEnabled = !rule.IsEnabled;
        await _repo.UpdateAsync(rule);
        return Ok(new { id, isEnabled = rule.IsEnabled });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await _repo.DeleteAsync(id);
        return deleted ? Ok(new { id }) : NotFound();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool ValidateRequest(UpsertTicketAlertRuleRequest request, out string error)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            error = "Title é obrigatório.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            error = "Message é obrigatório.";
            return false;
        }
        if (request.WorkflowStateId == Guid.Empty)
        {
            error = "WorkflowStateId é obrigatório.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static int? ResolveTimeout(UpsertTicketAlertRuleRequest request)
    {
        if (request.AlertType == PsadtAlertType.Modal)
            return null;
        var allowed = new[] { 5, 15, 30 };
        if (request.TimeoutSeconds.HasValue && allowed.Contains(request.TimeoutSeconds.Value))
            return request.TimeoutSeconds.Value;
        return 15;
    }
}

// ── DTO ───────────────────────────────────────────────────────────────────

public record UpsertTicketAlertRuleRequest(
    Guid WorkflowStateId,
    string Title,
    string Message,
    PsadtAlertType AlertType,
    int? TimeoutSeconds,
    string? ActionsJson,
    string? DefaultAction,
    string? Icon,
    AlertScopeType ScopePreference,
    bool? IsEnabled);
