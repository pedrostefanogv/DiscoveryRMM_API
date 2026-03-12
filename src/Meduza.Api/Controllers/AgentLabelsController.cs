using System.Text.Json;
using Meduza.Core.DTOs;
using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Meduza.Api.Controllers;

[ApiController]
[Route("api/agent-labels")]
public class AgentLabelsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAgentLabelRepository _labelRepository;
    private readonly IAgentLabelRuleRepository _ruleRepository;
    private readonly IAgentAutoLabelingService _autoLabelingService;

    public AgentLabelsController(
        IAgentLabelRepository labelRepository,
        IAgentLabelRuleRepository ruleRepository,
        IAgentAutoLabelingService autoLabelingService)
    {
        _labelRepository = labelRepository;
        _ruleRepository = ruleRepository;
        _autoLabelingService = autoLabelingService;
    }

    [HttpGet("agents/{agentId:guid}")]
    public async Task<IActionResult> GetByAgent(Guid agentId)
    {
        var labels = await _labelRepository.GetByAgentIdAsync(agentId);
        return Ok(labels);
    }

    [HttpGet("rules")]
    public async Task<IActionResult> GetRules([FromQuery] bool includeDisabled = true)
    {
        var rules = await _ruleRepository.GetAllAsync(includeDisabled);
        var result = rules.Select(MapRule).ToList();
        return Ok(result);
    }

    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule([FromBody] CreateAgentLabelRuleRequest request, CancellationToken cancellationToken)
    {
        var validationErrors = ValidateRulePayload(request.Name, request.Label, request.Expression);
        if (validationErrors.Count > 0)
            return BadRequest(new { Errors = validationErrors });

        var expressionJson = JsonSerializer.Serialize(request.Expression, JsonOptions);
        var createdBy = HttpContext.Items["Username"] as string ?? "api";

        var rule = await _ruleRepository.CreateAsync(new AgentLabelRule
        {
            Name = request.Name,
            Label = request.Label,
            ApplyMode = request.ApplyMode,
            IsEnabled = true,
            ExpressionJson = expressionJson,
            CreatedBy = createdBy,
            UpdatedBy = createdBy
        });

        await _autoLabelingService.ReprocessAllAgentsAsync($"rule-created:{rule.Id}", cancellationToken: cancellationToken);
        return Ok(MapRule(rule));
    }

    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] UpdateAgentLabelRuleRequest request, CancellationToken cancellationToken)
    {
        var validationErrors = ValidateRulePayload(request.Name, request.Label, request.Expression);
        if (validationErrors.Count > 0)
            return BadRequest(new { Errors = validationErrors });

        var existing = await _ruleRepository.GetByIdAsync(id);
        if (existing is null)
            return NotFound();

        existing.Name = request.Name;
        existing.Label = request.Label;
        existing.IsEnabled = request.IsEnabled;
        existing.ApplyMode = request.ApplyMode;
        existing.ExpressionJson = JsonSerializer.Serialize(request.Expression, JsonOptions);
        existing.UpdatedBy = HttpContext.Items["Username"] as string ?? "api";

        await _ruleRepository.UpdateAsync(existing);
        await _autoLabelingService.ReprocessAllAgentsAsync($"rule-updated:{id}", cancellationToken: cancellationToken);

        var updated = await _ruleRepository.GetByIdAsync(id);
        return Ok(MapRule(updated!));
    }

    [HttpDelete("rules/{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        await _ruleRepository.DeleteAsync(id);
        await _autoLabelingService.ReprocessAllAgentsAsync($"rule-deleted:{id}", cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpPost("reprocess")]
    public async Task<IActionResult> ReprocessAll(CancellationToken cancellationToken)
    {
        await _autoLabelingService.ReprocessAllAgentsAsync("manual-reprocess", cancellationToken: cancellationToken);
        return Ok(new { Message = "Reprocess finished." });
    }

    [HttpPost("rules/dry-run")]
    public async Task<IActionResult> DryRun([FromBody] AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken)
    {
        var expressionErrors = AgentLabelExpressionValidator.Validate(request.Expression);
        if (expressionErrors.Count > 0)
            return BadRequest(new { Errors = expressionErrors });

        if (!string.IsNullOrWhiteSpace(request.Label) && request.Label.Length > 120)
            return BadRequest(new { Errors = new[] { "Label exceeds maximum length of 120." } });

        try
        {
            var result = await _autoLabelingService.DryRunAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private static AgentLabelRuleResponse MapRule(AgentLabelRule rule)
    {
        var expression = DeserializeExpression(rule.ExpressionJson);
        return new AgentLabelRuleResponse
        {
            Id = rule.Id,
            Name = rule.Name,
            Label = rule.Label,
            IsEnabled = rule.IsEnabled,
            ApplyMode = rule.ApplyMode,
            Expression = expression,
            CreatedAt = rule.CreatedAt,
            UpdatedAt = rule.UpdatedAt
        };
    }

    private static AgentLabelRuleExpressionNodeDto DeserializeExpression(string expressionJson)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentLabelRuleExpressionNodeDto>(expressionJson, JsonOptions)
                ?? new AgentLabelRuleExpressionNodeDto();
        }
        catch (JsonException)
        {
            return new AgentLabelRuleExpressionNodeDto();
        }
    }

    private static List<string> ValidateRulePayload(string name, string label, AgentLabelRuleExpressionNodeDto expression)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(name))
            errors.Add("Name is required.");
        else if (name.Length > 200)
            errors.Add("Name exceeds maximum length of 200.");

        if (string.IsNullOrWhiteSpace(label))
            errors.Add("Label is required.");
        else if (label.Length > 120)
            errors.Add("Label exceeds maximum length of 120.");

        errors.AddRange(AgentLabelExpressionValidator.Validate(expression));
        return errors;
    }
}
