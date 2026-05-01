using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Api.Filters;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/agent-labels")]
public class AgentLabelsController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAgentLabelRepository _labelRepository;
    private readonly IAgentLabelRuleRepository _ruleRepository;
    private readonly IAgentAutoLabelingService _autoLabelingService;
    private readonly IRedisService _redisService;
    private readonly ICustomFieldService _customFieldService;

    public AgentLabelsController(
        IAgentLabelRepository labelRepository,
        IAgentLabelRuleRepository ruleRepository,
        IAgentAutoLabelingService autoLabelingService,
        IRedisService redisService,
        ICustomFieldService customFieldService)
    {
        _labelRepository = labelRepository;
        _ruleRepository = ruleRepository;
        _autoLabelingService = autoLabelingService;
        _redisService = redisService;
        _customFieldService = customFieldService;
    }

    [HttpGet("agents/{agentId:guid}")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = "Short")]
    public async Task<IActionResult> GetByAgent(Guid agentId)
    {
        var labels = await _labelRepository.GetByAgentIdAsync(agentId);
        return Ok(labels);
    }

    [HttpGet("rules/{ruleId:guid}/agents")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    public async Task<IActionResult> GetAgentsByRule(Guid ruleId)
    {
        var rule = await _ruleRepository.GetByIdAsync(ruleId);
        if (rule is null)
            return NotFound(new { Error = "Label rule not found." });

        var agents = await _labelRepository.GetAgentsByRuleIdAsync(ruleId);
        return Ok(new
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            Label = rule.Label,
            rule.Description,
            Agents = agents
        });
    }

    [HttpGet("rules")]
    [RequirePermission(ResourceType.Agents, ActionType.View)]
    [Microsoft.AspNetCore.OutputCaching.OutputCache(PolicyName = "Medium")]
    public async Task<IActionResult> GetRules([FromQuery] bool includeDisabled = true)
    {
        var rules = await _ruleRepository.GetAllAsync(includeDisabled);
        var result = rules.Select(MapRule).ToList();
        return Ok(result);
    }

    [HttpPost("rules")]
    [RequirePermission(ResourceType.Agents, ActionType.Create)]
    public async Task<IActionResult> CreateRule([FromBody] CreateAgentLabelRuleRequest request, CancellationToken cancellationToken)
    {
        var customFieldTypes = await BuildCustomFieldTypesAsync(cancellationToken);
        var validationErrors = await ValidateRulePayloadAsync(request.Name, request.Label, request.Description, request.Expression, customFieldTypes, cancellationToken);
        if (validationErrors.Count > 0)
            return BadRequest(new { Errors = validationErrors });

        var expressionJson = JsonSerializer.Serialize(request.Expression, JsonOptions);
        var createdBy = HttpContext.Items["Username"] as string ?? "api";

        var rule = await _ruleRepository.CreateAsync(new AgentLabelRule
        {
            Name = request.Name,
            Label = request.Label,
            Description = request.Description,
            ApplyMode = request.ApplyMode,
            IsEnabled = true,
            ExpressionJson = expressionJson,
            CreatedBy = createdBy,
            UpdatedBy = createdBy
        });

        await InvalidateRuleCachesAsync();
        await _autoLabelingService.ReprocessAllAgentsAsync($"rule-created:{rule.Id}", cancellationToken: cancellationToken);
        return Ok(MapRule(rule));
    }

    [HttpPut("rules/{id:guid}")]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] UpdateAgentLabelRuleRequest request, CancellationToken cancellationToken)
    {
        var customFieldTypes = await BuildCustomFieldTypesAsync(cancellationToken);
        var validationErrors = await ValidateRulePayloadAsync(request.Name, request.Label, request.Description, request.Expression, customFieldTypes, cancellationToken);
        if (validationErrors.Count > 0)
            return BadRequest(new { Errors = validationErrors });

        var existing = await _ruleRepository.GetByIdAsync(id);
        if (existing is null)
            return NotFound();

        existing.Name = request.Name;
        existing.Label = request.Label;
        existing.Description = request.Description;
        existing.IsEnabled = request.IsEnabled;
        existing.ApplyMode = request.ApplyMode;
        existing.ExpressionJson = JsonSerializer.Serialize(request.Expression, JsonOptions);
        existing.UpdatedBy = HttpContext.Items["Username"] as string ?? "api";

        await _ruleRepository.UpdateAsync(existing);
        await InvalidateRuleCachesAsync();
        await _autoLabelingService.ReprocessAllAgentsAsync($"rule-updated:{id}", cancellationToken: cancellationToken);

        var updated = await _ruleRepository.GetByIdAsync(id);
        return Ok(MapRule(updated!));
    }

    [HttpDelete("rules/{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken cancellationToken)
    {
        await _ruleRepository.DeleteAsync(id);
        await InvalidateRuleCachesAsync();
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
        var customFieldTypes = await BuildCustomFieldTypesAsync(cancellationToken);
        var expressionErrors = AgentLabelExpressionValidator.Validate(request.Expression, customFieldTypes);
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

    [HttpGet("rules/available-custom-fields")]
    public async Task<IActionResult> GetAvailableCustomFields(CancellationToken cancellationToken)
    {
        var definitions = await _customFieldService.GetDefinitionsAsync(
            scopeType: null,
            includeInactive: false,
            cancellationToken: cancellationToken);

        var result = definitions
            .Where(d => d.ScopeType is CustomFieldScopeType.Agent
                            or CustomFieldScopeType.Client
                            or CustomFieldScopeType.Site)
            .Select(d => new LabelRuleCustomFieldSummaryDto
            {
                Id = d.Id,
                Name = d.Name,
                Label = d.Label,
                Description = d.Description,
                ScopeType = d.ScopeType,
                DataType = d.DataType
            })
            .ToList();

        return Ok(result);
    }

    private static AgentLabelRuleResponse MapRule(AgentLabelRule rule)
    {
        var expression = DeserializeExpression(rule.ExpressionJson);
        return new AgentLabelRuleResponse
        {
            Id = rule.Id,
            Name = rule.Name,
            Label = rule.Label,
            Description = rule.Description,
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

    private static Task<List<string>> ValidateRulePayloadAsync(
        string name,
        string label,
        string? description,
        AgentLabelRuleExpressionNodeDto expression,
        IReadOnlyDictionary<Guid, CustomFieldDataType>? customFieldTypes,
        CancellationToken _)
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

        if (!string.IsNullOrWhiteSpace(description) && description.Length > 2000)
            errors.Add("Description exceeds maximum length of 2000.");

        errors.AddRange(AgentLabelExpressionValidator.Validate(expression, customFieldTypes));
        return Task.FromResult(errors);
    }

    private async Task<IReadOnlyDictionary<Guid, CustomFieldDataType>> BuildCustomFieldTypesAsync(
        CancellationToken cancellationToken)
    {
        var definitions = await _customFieldService.GetDefinitionsAsync(
            scopeType: null,
            includeInactive: false,
            cancellationToken: cancellationToken);

        return definitions
            .Where(d => d.ScopeType is CustomFieldScopeType.Agent
                            or CustomFieldScopeType.Client
                            or CustomFieldScopeType.Site)
            .ToDictionary(d => d.Id, d => d.DataType);
    }

    private Task InvalidateRuleCachesAsync()
        => _redisService.DeleteAsync("label-rules:enabled");
}
