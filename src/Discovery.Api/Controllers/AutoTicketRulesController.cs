using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/auto-ticket-rules")]
public class AutoTicketRulesController : ControllerBase
{
    private readonly IAutoTicketRuleRepository _ruleRepository;
    private readonly IAutoTicketRuleExecutionRepository _executionRepository;
    private readonly IAutoTicketRuleEngineService _ruleEngineService;
    private readonly IDedupFingerprintService _dedupFingerprintService;
    private readonly IMonitoringEventNormalizationService _normalizationService;
    private readonly IAgentLabelRepository _agentLabelRepository;
    private readonly IDepartmentRepository _departmentRepository;
    private readonly IWorkflowProfileRepository _workflowProfileRepository;

    public AutoTicketRulesController(
        IAutoTicketRuleRepository ruleRepository,
        IAutoTicketRuleExecutionRepository executionRepository,
        IAutoTicketRuleEngineService ruleEngineService,
        IDedupFingerprintService dedupFingerprintService,
        IMonitoringEventNormalizationService normalizationService,
        IAgentLabelRepository agentLabelRepository,
        IDepartmentRepository departmentRepository,
        IWorkflowProfileRepository workflowProfileRepository)
    {
        _ruleRepository = ruleRepository;
        _executionRepository = executionRepository;
        _ruleEngineService = ruleEngineService;
        _dedupFingerprintService = dedupFingerprintService;
        _normalizationService = normalizationService;
        _agentLabelRepository = agentLabelRepository;
        _departmentRepository = departmentRepository;
        _workflowProfileRepository = workflowProfileRepository;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] AutoTicketScopeLevel? scopeLevel,
        [FromQuery] Guid? scopeId,
        [FromQuery] bool? isEnabled,
        [FromQuery] string? alertCode)
    {
        var rules = await _ruleRepository.GetAllAsync(scopeLevel, scopeId, isEnabled, alertCode);
        return Ok(rules.Select(MapRule));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var rule = await _ruleRepository.GetByIdAsync(id);
        return rule is null ? NotFound() : Ok(MapRule(rule));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertAutoTicketRuleRequest request)
    {
        if (!ValidateRequest(request, out var error))
            return BadRequest(new { error });

        var rule = MapRequest(request, new AutoTicketRule());
        var created = await _ruleRepository.CreateAsync(rule);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapRule(created));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertAutoTicketRuleRequest request)
    {
        if (!ValidateRequest(request, out var error))
            return BadRequest(new { error });

        var existing = await _ruleRepository.GetByIdAsync(id);
        if (existing is null)
            return NotFound();

        var updated = await _ruleRepository.UpdateAsync(MapRequest(request, existing));
        return Ok(MapRule(updated));
    }

    [HttpPatch("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id)
        => await SetEnabledAsync(id, true);

    [HttpPatch("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id)
        => await SetEnabledAsync(id, false);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var deleted = await _ruleRepository.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/dry-run")]
    public async Task<IActionResult> DryRun(Guid id, [FromBody] AutoTicketRuleDryRunRequest request, CancellationToken cancellationToken)
    {
        var rule = await _ruleRepository.GetByIdAsync(id);
        if (rule is null)
            return NotFound(new { error = "Regra não encontrada." });

        if (request.ClientId == Guid.Empty || request.AgentId == Guid.Empty || string.IsNullOrWhiteSpace(request.AlertCode))
            return BadRequest(new { error = "ClientId, AgentId e AlertCode são obrigatórios." });

        var labels = await ResolveLabelsAsync(request.AgentId, request.Labels);
        var monitoringEvent = new AgentMonitoringEvent
        {
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            AgentId = request.AgentId,
            AlertCode = request.AlertCode.Trim(),
            Severity = request.Severity,
            Title = request.Title ?? $"[DryRun] {request.AlertCode}",
            Message = request.Message ?? $"Dry-run for alert '{request.AlertCode}'.",
            MetricKey = request.MetricKey,
            MetricValue = request.MetricValue,
            PayloadJson = request.PayloadJson,
            LabelsSnapshotJson = _normalizationService.SerializeLabels(labels),
            Source = request.Source,
            OccurredAt = request.OccurredAt ?? DateTime.UtcNow
        };

        var decision = await _ruleEngineService.EvaluateAsync(
            monitoringEvent,
            labels,
            [rule],
            cancellationToken);

        return Ok(new AutoTicketRuleDryRunResponse
        {
            Decision = decision.IsSuppressed ? AutoTicketDecision.Suppressed : decision.Decision,
            WouldCreateTicket = decision.ShouldCreateTicket,
            RuleId = decision.Rule?.Id,
            RuleName = decision.Rule?.Name,
            Reason = decision.Reason,
            DedupKey = decision.ShouldCreateTicket && decision.Rule is not null
                ? _dedupFingerprintService.BuildDedupKey(monitoringEvent, decision.Rule)
                : null
        });
    }

    [HttpPost("seed-defaults")]
    public async Task<IActionResult> SeedDefaults([FromBody] SeedDefaultAutoTicketRulesRequest request)
    {
        if (request.ClientId == Guid.Empty)
            return BadRequest(new { error = "ClientId é obrigatório." });

        var departments = await _departmentRepository.GetByClientAsync(request.ClientId, includeGlobal: true, activeOnly: true);
        var infraDepartment = FindDepartmentByName(departments, request.InfraDepartmentName);
        var serviceDeskDepartment = FindDepartmentByName(departments, request.ServiceDeskDepartmentName);

        var errors = new List<string>();
        if (infraDepartment is null)
            errors.Add($"Departamento '{request.InfraDepartmentName}' não encontrado para o cliente informado.");

        if (serviceDeskDepartment is null)
            errors.Add($"Departamento '{request.ServiceDeskDepartmentName}' não encontrado para o cliente informado.");

        if (errors.Count > 0)
            return BadRequest(new { error = "Não foi possível seedar as regras padrão.", details = errors });

        var existingRules = await _ruleRepository.GetAllAsync(
            scopeLevel: AutoTicketScopeLevel.Client,
            scopeId: request.ClientId,
            isEnabled: null,
            alertCode: "disk.full");

        var seededRules = new List<SeededAutoTicketRuleResult>
        {
            await UpsertSeedRuleAsync(
                existingRules,
                request,
                name: "AutoTicket Default :: disk.full :: servidor",
                label: "servidor",
                priorityOrder: 200,
                department: infraDepartment!,
                category: "Capacity",
                priority: TicketPriority.Critical),
            await UpsertSeedRuleAsync(
                existingRules,
                request,
                name: "AutoTicket Default :: disk.full :: pc-comum",
                label: "pc-comum",
                priorityOrder: 150,
                department: serviceDeskDepartment!,
                category: "Endpoint",
                priority: TicketPriority.Low)
        };

        return Ok(new SeedDefaultAutoTicketRulesResponse
        {
            CreatedCount = seededRules.Count(rule => string.Equals(rule.Status, "created", StringComparison.OrdinalIgnoreCase)),
            UpdatedCount = seededRules.Count(rule => string.Equals(rule.Status, "updated", StringComparison.OrdinalIgnoreCase)),
            Rules = seededRules
        });
    }

    [HttpGet("{id:guid}/stats")]
    public async Task<IActionResult> GetStats(Guid id, [FromQuery] int hours = 24)
    {
        var rule = await _ruleRepository.GetByIdAsync(id);
        if (rule is null)
            return NotFound(new { error = "Regra não encontrada." });

        var safeHours = Math.Clamp(hours, 1, 24 * 30);
        var periodEndUtc = DateTime.UtcNow;
        var periodStartUtc = periodEndUtc.AddHours(-safeHours);
        var snapshot = await _executionRepository.GetRuleStatsAsync(rule, periodStartUtc, periodEndUtc);

        return Ok(new AutoTicketRuleStatsResponse
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            ScopeLevel = rule.ScopeLevel,
            ScopeId = rule.ScopeId,
            AlertCodeFilter = rule.AlertCodeFilter,
            PeriodHours = safeHours,
            PeriodStartUtc = periodStartUtc,
            PeriodEndUtc = periodEndUtc,
            TotalEvaluations = snapshot.TotalEvaluations,
            SelectedExecutions = snapshot.SelectedExecutions,
            CreatedCount = snapshot.CreatedCount,
            DedupedCount = snapshot.DedupedCount,
            SuppressedCount = snapshot.SuppressedCount,
            MatchedNoActionCount = snapshot.MatchedNoActionCount,
            FailedCount = snapshot.FailedCount,
            RateLimitedCount = snapshot.RateLimitedCount,
            MatchRate = CalculateRate(snapshot.SelectedExecutions, snapshot.TotalEvaluations),
            DedupRate = CalculateRate(snapshot.DedupedCount, snapshot.SelectedExecutions),
            CreateRate = CalculateRate(snapshot.CreatedCount, snapshot.SelectedExecutions),
            FailureRate = CalculateRate(snapshot.FailedCount, snapshot.SelectedExecutions),
            FirstSelectedAtUtc = snapshot.FirstSelectedAtUtc,
            LastSelectedAtUtc = snapshot.LastSelectedAtUtc
        });
    }

    private async Task<IActionResult> SetEnabledAsync(Guid id, bool enabled)
    {
        var existing = await _ruleRepository.GetByIdAsync(id);
        if (existing is null)
            return NotFound();

        existing.IsEnabled = enabled;
        var updated = await _ruleRepository.UpdateAsync(existing);
        return Ok(MapRule(updated));
    }

    private AutoTicketRule MapRequest(UpsertAutoTicketRuleRequest request, AutoTicketRule target)
    {
        target.Name = request.Name.Trim();
        target.IsEnabled = request.IsEnabled;
        target.PriorityOrder = request.PriorityOrder;
        target.ScopeLevel = request.ScopeLevel;
        target.ScopeId = request.ScopeLevel == AutoTicketScopeLevel.Global ? null : request.ScopeId;
        target.AlertCodeFilter = string.IsNullOrWhiteSpace(request.AlertCodeFilter) ? null : request.AlertCodeFilter.Trim();
        target.SourceFilter = request.SourceFilter;
        target.SeverityMin = request.SeverityMin;
        target.SeverityMax = request.SeverityMax;
        target.MatchLabelsAnyJson = _normalizationService.SerializeLabels(request.MatchLabelsAny);
        target.MatchLabelsAllJson = _normalizationService.SerializeLabels(request.MatchLabelsAll);
        target.ExcludeLabelsJson = _normalizationService.SerializeLabels(request.ExcludeLabels);
        target.PayloadPredicateJson = string.IsNullOrWhiteSpace(request.PayloadPredicateJson) ? null : request.PayloadPredicateJson;
        target.Action = request.Action;
        target.TargetDepartmentId = request.TargetDepartmentId;
        target.TargetWorkflowProfileId = request.TargetWorkflowProfileId;
        target.TargetCategory = string.IsNullOrWhiteSpace(request.TargetCategory) ? null : request.TargetCategory.Trim();
        target.TargetPriority = request.TargetPriority;
        target.DedupWindowMinutes = Math.Max(1, request.DedupWindowMinutes);
        target.CooldownMinutes = Math.Max(0, request.CooldownMinutes);
        return target;
    }

    private async Task<SeededAutoTicketRuleResult> UpsertSeedRuleAsync(
        IReadOnlyCollection<AutoTicketRule> existingRules,
        SeedDefaultAutoTicketRulesRequest request,
        string name,
        string label,
        int priorityOrder,
        Department department,
        string category,
        TicketPriority priority)
    {
        var workflowProfile = await _workflowProfileRepository.GetDefaultByDepartmentAsync(department.Id);
        var existing = FindExistingSeedRule(existingRules, "disk.full", label);

        var target = existing ?? new AutoTicketRule();
        target.Name = name;
        target.IsEnabled = request.IsEnabled;
        target.PriorityOrder = priorityOrder;
        target.ScopeLevel = AutoTicketScopeLevel.Client;
        target.ScopeId = request.ClientId;
        target.AlertCodeFilter = "disk.full";
        target.SourceFilter = null;
        target.SeverityMin = null;
        target.SeverityMax = null;
        target.MatchLabelsAnyJson = null;
        target.MatchLabelsAllJson = _normalizationService.SerializeLabels([label]);
        target.ExcludeLabelsJson = null;
        target.PayloadPredicateJson = null;
        target.Action = AutoTicketRuleAction.CreateTicket;
        target.TargetDepartmentId = department.Id;
        target.TargetWorkflowProfileId = workflowProfile?.Id;
        target.TargetCategory = category;
        target.TargetPriority = priority;
        target.DedupWindowMinutes = Math.Max(1, request.DedupWindowMinutes);
        target.CooldownMinutes = 0;

        var persisted = existing is null
            ? await _ruleRepository.CreateAsync(target)
            : await _ruleRepository.UpdateAsync(target);

        return new SeededAutoTicketRuleResult
        {
            RuleId = persisted.Id,
            Name = persisted.Name,
            Label = label,
            Status = existing is null ? "created" : "updated",
            DepartmentId = persisted.TargetDepartmentId,
            WorkflowProfileId = persisted.TargetWorkflowProfileId,
            Warning = workflowProfile is null
                ? $"Nenhum workflow profile ativo foi encontrado para o departamento '{department.Name}'."
                : null
        };
    }

    private object MapRule(AutoTicketRule rule)
    {
        return new
        {
            rule.Id,
            rule.Name,
            rule.IsEnabled,
            rule.PriorityOrder,
            rule.ScopeLevel,
            rule.ScopeId,
            rule.AlertCodeFilter,
            rule.SourceFilter,
            rule.SeverityMin,
            rule.SeverityMax,
            MatchLabelsAny = _normalizationService.DeserializeLabels(rule.MatchLabelsAnyJson),
            MatchLabelsAll = _normalizationService.DeserializeLabels(rule.MatchLabelsAllJson),
            ExcludeLabels = _normalizationService.DeserializeLabels(rule.ExcludeLabelsJson),
            rule.PayloadPredicateJson,
            rule.Action,
            rule.TargetDepartmentId,
            rule.TargetWorkflowProfileId,
            rule.TargetCategory,
            rule.TargetPriority,
            rule.DedupWindowMinutes,
            rule.CooldownMinutes,
            rule.CreatedAt,
            rule.UpdatedAt
        };
    }

    private async Task<IReadOnlyCollection<string>> ResolveLabelsAsync(Guid agentId, IReadOnlyCollection<string>? requestedLabels)
    {
        if (requestedLabels is not null && requestedLabels.Count > 0)
            return _normalizationService.DeserializeLabels(_normalizationService.SerializeLabels(requestedLabels));

        var labels = await _agentLabelRepository.GetByAgentIdAsync(agentId);
        return labels
            .Select(label => label.Label)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private AutoTicketRule? FindExistingSeedRule(IReadOnlyCollection<AutoTicketRule> rules, string alertCode, string label)
    {
        return rules.FirstOrDefault(rule =>
            string.Equals(rule.AlertCodeFilter, alertCode, StringComparison.OrdinalIgnoreCase) &&
            _normalizationService.DeserializeLabels(rule.MatchLabelsAllJson)
                .Contains(label, StringComparer.OrdinalIgnoreCase));
    }

    private static Department? FindDepartmentByName(IEnumerable<Department> departments, string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
            return null;

        return departments.FirstOrDefault(department =>
            string.Equals(department.Name?.Trim(), requestedName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static double CalculateRate(int numerator, int denominator)
        => denominator <= 0 ? 0 : Math.Round((numerator / (double)denominator) * 100.0, 2);

    private static bool ValidateRequest(UpsertAutoTicketRuleRequest request, out string error)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "Name é obrigatório.";
            return false;
        }

        if (request.ScopeLevel != AutoTicketScopeLevel.Global && !request.ScopeId.HasValue)
        {
            error = "ScopeId é obrigatório para regras Client ou Site.";
            return false;
        }

        if (request.SeverityMin.HasValue && request.SeverityMax.HasValue && request.SeverityMin > request.SeverityMax)
        {
            error = "SeverityMin não pode ser maior que SeverityMax.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}