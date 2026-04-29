using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Discovery.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/monitoring-events")]
public class MonitoringEventsController : ControllerBase
{
    private readonly IAgentMonitoringEventRepository _monitoringEventRepository;
    private readonly IAutoTicketRuleExecutionRepository _executionRepository;
    private readonly IAutoTicketOrchestratorService _orchestratorService;
    private readonly IAgentLabelRepository _agentLabelRepository;
    private readonly IMonitoringEventNormalizationService _normalizationService;

    public MonitoringEventsController(
        IAgentMonitoringEventRepository monitoringEventRepository,
        IAutoTicketRuleExecutionRepository executionRepository,
        IAutoTicketOrchestratorService orchestratorService,
        IAgentLabelRepository agentLabelRepository,
        IMonitoringEventNormalizationService normalizationService)
    {
        _monitoringEventRepository = monitoringEventRepository;
        _executionRepository = executionRepository;
        _orchestratorService = orchestratorService;
        _agentLabelRepository = agentLabelRepository;
        _normalizationService = normalizationService;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMonitoringEventRequest request, CancellationToken cancellationToken)
    {
        if (!ValidateRequest(request, out var error))
            return BadRequest(new { error });

        var labels = await ResolveLabelsAsync(request.AgentId, request.Labels);
        var monitoringEvent = await _monitoringEventRepository.CreateAsync(new AgentMonitoringEvent
        {
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            AgentId = request.AgentId,
            AlertCode = request.AlertCode.Trim(),
            Severity = request.Severity,
            Title = string.IsNullOrWhiteSpace(request.Title) ? $"[Monitoring] {request.AlertCode}" : request.Title.Trim(),
            Message = string.IsNullOrWhiteSpace(request.Message) ? $"Monitoring event '{request.AlertCode}' ingested manually." : request.Message.Trim(),
            MetricKey = request.MetricKey,
            MetricValue = request.MetricValue,
            PayloadJson = request.PayloadJson,
            LabelsSnapshotJson = _normalizationService.SerializeLabels(labels),
            Source = request.Source,
            SourceRefId = request.SourceRefId,
            CorrelationId = request.CorrelationId,
            OccurredAt = request.OccurredAt ?? DateTime.UtcNow
        });

        if (!request.EvaluateAutoTicket)
            return Ok(new MonitoringEventIngestionResponse { MonitoringEventId = monitoringEvent.Id });

        var execution = await _orchestratorService.EvaluateAsync(monitoringEvent, cancellationToken);
        return Ok(new MonitoringEventIngestionResponse
        {
            MonitoringEventId = monitoringEvent.Id,
            RuleId = execution.RuleId,
            Decision = execution.Decision,
            CreatedTicketId = execution.CreatedTicketId,
            Reason = execution.Reason
        });
    }

    [HttpPost("{id:guid}/evaluate")]
    public async Task<IActionResult> Evaluate(Guid id, CancellationToken cancellationToken)
    {
        var monitoringEvent = await _monitoringEventRepository.GetByIdAsync(id);
        if (monitoringEvent is null)
            return NotFound(new { error = "Evento de monitoramento não encontrado." });

        var execution = await _orchestratorService.EvaluateAsync(monitoringEvent, cancellationToken);
        return Ok(new MonitoringEventIngestionResponse
        {
            MonitoringEventId = monitoringEvent.Id,
            RuleId = execution.RuleId,
            Decision = execution.Decision,
            CreatedTicketId = execution.CreatedTicketId,
            Reason = execution.Reason
        });
    }

    [HttpGet("{id:guid}/auto-ticket-decisions")]
    public async Task<IActionResult> GetDecisions(Guid id)
    {
        var monitoringEvent = await _monitoringEventRepository.GetByIdAsync(id);
        if (monitoringEvent is null)
            return NotFound(new { error = "Evento de monitoramento não encontrado." });

        var executions = await _executionRepository.GetByMonitoringEventIdAsync(id);
        return Ok(executions);
    }

    private async Task<IReadOnlyCollection<string>> ResolveLabelsAsync(Guid agentId, IReadOnlyCollection<string>? requestedLabels)
    {
        if (requestedLabels is not null && requestedLabels.Count > 0)
            return _normalizationService.DeserializeLabels(_normalizationService.SerializeLabels(requestedLabels));

        var labels = await _agentLabelRepository.GetByAgentIdAsync(agentId);
        return labels.Select(label => label.Label).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    private static bool ValidateRequest(CreateMonitoringEventRequest request, out string error)
    {
        if (request.ClientId == Guid.Empty)
        {
            error = "ClientId é obrigatório.";
            return false;
        }

        if (request.AgentId == Guid.Empty)
        {
            error = "AgentId é obrigatório.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.AlertCode))
        {
            error = "AlertCode é obrigatório.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}