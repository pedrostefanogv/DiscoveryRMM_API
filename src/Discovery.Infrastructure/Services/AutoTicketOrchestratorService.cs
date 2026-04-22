using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

public class AutoTicketOrchestratorService : IAutoTicketOrchestratorService
{
    private static readonly Meter Meter = new("Discovery.AutoTicket");
    private static readonly Counter<long> EvaluatedCounter = Meter.CreateCounter<long>("auto_ticket_evaluated_total");
    private static readonly Counter<long> CreatedCounter = Meter.CreateCounter<long>("auto_ticket_created_total");
    private static readonly Counter<long> DedupedCounter = Meter.CreateCounter<long>("auto_ticket_deduped_total");
    private static readonly Counter<long> FailedCounter = Meter.CreateCounter<long>("auto_ticket_failed_total");
    private static readonly Counter<long> RateLimitedCounter = Meter.CreateCounter<long>("auto_ticket_rate_limited_total");
    private static readonly Histogram<double> EvalDurationMs = Meter.CreateHistogram<double>("auto_ticket_eval_duration_ms");

    private readonly IAutoTicketRuleEngineService _ruleEngineService;
    private readonly IAutoTicketDedupService _dedupService;
    private readonly IAutoTicketRuleExecutionRepository _executionRepository;
    private readonly IAlertToTicketService _alertToTicketService;
    private readonly ITicketRepository _ticketRepository;
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IActivityLogService _activityLogService;
    private readonly IMonitoringEventNormalizationService _normalizationService;
    private readonly IDedupFingerprintService _dedupFingerprintService;
    private readonly DiscoveryDbContext _db;
    private readonly AutoTicketOptions _options;
    private readonly ILogger<AutoTicketOrchestratorService> _logger;

    public AutoTicketOrchestratorService(
        IAutoTicketRuleEngineService ruleEngineService,
        IAutoTicketDedupService dedupService,
        IAutoTicketRuleExecutionRepository executionRepository,
        IAlertToTicketService alertToTicketService,
        ITicketRepository ticketRepository,
        IWorkflowRepository workflowRepository,
        IActivityLogService activityLogService,
        IMonitoringEventNormalizationService normalizationService,
        IDedupFingerprintService dedupFingerprintService,
        DiscoveryDbContext db,
        IOptions<AutoTicketOptions> options,
        ILogger<AutoTicketOrchestratorService> logger)
    {
        _ruleEngineService = ruleEngineService;
        _dedupService = dedupService;
        _executionRepository = executionRepository;
        _alertToTicketService = alertToTicketService;
        _ticketRepository = ticketRepository;
        _workflowRepository = workflowRepository;
        _activityLogService = activityLogService;
        _normalizationService = normalizationService;
        _dedupFingerprintService = dedupFingerprintService;
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AutoTicketRuleExecution> EvaluateAsync(AgentMonitoringEvent monitoringEvent, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        AutoTicketRuleExecution? execution = null;
        AutoTicketRule? matchedRule = null;

        try
        {
            var labels = _normalizationService.DeserializeLabels(monitoringEvent.LabelsSnapshotJson);
            var decision = await _ruleEngineService.EvaluateAsync(monitoringEvent, labels, cancellationToken: cancellationToken);
            matchedRule = decision.Rule;

            if (!decision.Matched)
            {
                execution = await CreateExecutionAsync(
                    monitoringEvent,
                    null,
                    AutoTicketDecision.MatchedNoAction,
                    decision.Reason,
                    null,
                    null,
                    false);
                return execution;
            }

            if (decision.IsSuppressed)
            {
                execution = await CreateExecutionAsync(
                    monitoringEvent,
                    decision.Rule,
                    AutoTicketDecision.Suppressed,
                    decision.Reason,
                    null,
                    null,
                    false);
                return execution;
            }

            if (!decision.ShouldCreateTicket)
            {
                execution = await CreateExecutionAsync(
                    monitoringEvent,
                    decision.Rule,
                    AutoTicketDecision.MatchedNoAction,
                    decision.Reason,
                    null,
                    null,
                    false);
                return execution;
            }

            if (!_options.Enabled)
            {
                execution = await CreateExecutionAsync(
                    monitoringEvent,
                    decision.Rule,
                    AutoTicketDecision.MatchedNoAction,
                    "AutoTicket is disabled by configuration.",
                    null,
                    null,
                    false);
                return execution;
            }

            var dedupKey = _dedupFingerprintService.BuildDedupKey(monitoringEvent, decision.Rule!);
            if (!CanCreateTicketsFor(monitoringEvent.ClientId, monitoringEvent.SiteId))
            {
                execution = await CreateExecutionAsync(
                    monitoringEvent,
                    decision.Rule,
                    AutoTicketDecision.MatchedNoAction,
                    _options.ShadowMode ? "AutoTicket shadow mode is active." : "Monitoring event is outside of the configured canary scope.",
                    null,
                    dedupKey,
                    false);
                return execution;
            }

            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var dedupResult = await _dedupService.TryAcquireOrGetAsync(
                dedupKey,
                TimeSpan.FromMinutes(Math.Max(1, decision.Rule!.DedupWindowMinutes)),
                cancellationToken);

            if (!dedupResult.Acquired)
            {
                execution = await CreateExecutionAsync(
                    monitoringEvent,
                    decision.Rule,
                    AutoTicketDecision.Deduped,
                    "Existing correlation lock found inside dedup window.",
                    dedupResult.ExistingTicketId,
                    dedupKey,
                    true);
                await transaction.CommitAsync(cancellationToken);
                return execution;
            }

            var ticketRequest = BuildTicketRequest(monitoringEvent, decision.Rule, labels);
            var reusableOpenTicketId = await _executionRepository.GetReusableOpenTicketIdAsync(
                monitoringEvent.ClientId,
                monitoringEvent.AgentId,
                monitoringEvent.AlertCode,
                ticketRequest.DepartmentId,
                ticketRequest.WorkflowProfileId,
                ticketRequest.Category);

            if (reusableOpenTicketId.HasValue)
            {
                await _dedupService.RegisterCreatedTicketAsync(dedupKey, reusableOpenTicketId.Value, cancellationToken);

                execution = await CreateExecutionAsync(
                    monitoringEvent,
                    decision.Rule,
                    AutoTicketDecision.Deduped,
                    "Existing open AutoTicket was found for the same agent and alert type.",
                    reusableOpenTicketId.Value,
                    dedupKey,
                    true);

                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "AutoTicket reused existing open ticket {TicketId} for monitoring event {MonitoringEventId} using dedupKey {DedupKey}.",
                    reusableOpenTicketId.Value,
                    monitoringEvent.Id,
                    dedupKey);

                return execution;
            }

            if (_options.ReopenWindowMinutes > 0)
            {
                var reopenableClosedTicketId = await _executionRepository.GetReopenableClosedTicketIdAsync(
                    monitoringEvent.ClientId,
                    monitoringEvent.AgentId,
                    monitoringEvent.AlertCode,
                    DateTime.UtcNow.AddMinutes(-_options.ReopenWindowMinutes),
                    ticketRequest.DepartmentId,
                    ticketRequest.WorkflowProfileId,
                    ticketRequest.Category);

                if (reopenableClosedTicketId.HasValue)
                {
                    var reopenableTicket = await _ticketRepository.GetByIdAsync(reopenableClosedTicketId.Value);
                    var initialState = await _workflowRepository.GetInitialStateAsync(monitoringEvent.ClientId);

                    if (reopenableTicket is not null && reopenableTicket.ClosedAt.HasValue && initialState is not null)
                    {
                        var previousClosedAt = reopenableTicket.ClosedAt.Value;
                        await _ticketRepository.UpdateWorkflowStateAsync(reopenableTicket.Id, initialState.Id, closedAt: null);
                        await _activityLogService.LogActivityAsync(
                            reopenableTicket.Id,
                            TicketActivityType.Reopened,
                            null,
                            previousClosedAt.ToString("O"),
                            null,
                            $"Ticket reaberto automaticamente a partir do evento de monitoramento {monitoringEvent.Id}: {monitoringEvent.AlertCode}");
                        await _dedupService.RegisterCreatedTicketAsync(dedupKey, reopenableTicket.Id, cancellationToken);

                        execution = await CreateExecutionAsync(
                            monitoringEvent,
                            decision.Rule,
                            AutoTicketDecision.Deduped,
                            $"Closed AutoTicket was reopened inside the configured reopen window ({_options.ReopenWindowMinutes} min).",
                            reopenableTicket.Id,
                            dedupKey,
                            true);

                        await transaction.CommitAsync(cancellationToken);

                        _logger.LogInformation(
                            "AutoTicket reopened closed ticket {TicketId} for monitoring event {MonitoringEventId} using dedupKey {DedupKey}.",
                            reopenableTicket.Id,
                            monitoringEvent.Id,
                            dedupKey);

                        return execution;
                    }
                }
            }

            if (_options.MaxCreatedTicketsPerHourPerAlertCode > 0)
            {
                var createdInLastHour = await _executionRepository.GetCreatedCountForClientAlertAsync(
                    monitoringEvent.ClientId,
                    monitoringEvent.AlertCode,
                    DateTime.UtcNow.AddHours(-1));

                if (createdInLastHour >= _options.MaxCreatedTicketsPerHourPerAlertCode)
                {
                    execution = await CreateExecutionAsync(
                        monitoringEvent,
                        decision.Rule,
                        AutoTicketDecision.RateLimited,
                        $"Client alert rate limit reached for '{monitoringEvent.AlertCode}' ({_options.MaxCreatedTicketsPerHourPerAlertCode} tickets/hour).",
                        null,
                        dedupKey,
                        false);

                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogWarning(
                        "AutoTicket rate limit reached for client {ClientId}, alertCode {AlertCode}. Limit={LimitPerHour}, monitoringEventId={MonitoringEventId}.",
                        monitoringEvent.ClientId,
                        monitoringEvent.AlertCode,
                        _options.MaxCreatedTicketsPerHourPerAlertCode,
                        monitoringEvent.Id);

                    return execution;
                }
            }

            var ticket = await _alertToTicketService.CreateTicketFromMonitoringEventAsync(
                ticketRequest,
                cancellationToken);

            await _dedupService.RegisterCreatedTicketAsync(dedupKey, ticket.Id, cancellationToken);

            execution = await CreateExecutionAsync(
                monitoringEvent,
                decision.Rule,
                AutoTicketDecision.Created,
                decision.Reason,
                ticket.Id,
                dedupKey,
                false);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "AutoTicket created ticket {TicketId} for monitoring event {MonitoringEventId} using rule {RuleId} and dedupKey {DedupKey}.",
                ticket.Id,
                monitoringEvent.Id,
                decision.Rule.Id,
                dedupKey);

            return execution;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoTicket failed for monitoring event {MonitoringEventId} ({AlertCode}).", monitoringEvent.Id, monitoringEvent.AlertCode);
            execution = await CreateExecutionAsync(
                monitoringEvent,
                matchedRule,
                AutoTicketDecision.Failed,
                ex.Message,
                null,
                null,
                false);
            return execution;
        }
        finally
        {
            stopwatch.Stop();

            var tags = new TagList
            {
                { "decision", execution?.Decision.ToString() ?? "Unknown" },
                { "alertCode", monitoringEvent.AlertCode }
            };

            EvaluatedCounter.Add(1, tags);
            EvalDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds, tags);

            switch (execution?.Decision)
            {
                case AutoTicketDecision.Created:
                    CreatedCounter.Add(1, tags);
                    break;
                case AutoTicketDecision.Deduped:
                    DedupedCounter.Add(1, tags);
                    break;
                case AutoTicketDecision.Failed:
                    FailedCounter.Add(1, tags);
                    break;
                case AutoTicketDecision.RateLimited:
                    RateLimitedCounter.Add(1, tags);
                    break;
            }
        }
    }

    private async Task<AutoTicketRuleExecution> CreateExecutionAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRule? rule,
        AutoTicketDecision decision,
        string reason,
        Guid? createdTicketId,
        string? dedupKey,
        bool dedupHit)
    {
        return await _executionRepository.CreateAsync(new AutoTicketRuleExecution
        {
            RuleId = rule?.Id,
            MonitoringEventId = monitoringEvent.Id,
            AgentId = monitoringEvent.AgentId,
            EvaluatedAt = DateTime.UtcNow,
            Decision = decision,
            Reason = reason,
            CreatedTicketId = createdTicketId,
            DedupKey = dedupKey,
            DedupHit = dedupHit,
            PayloadSnapshotJson = monitoringEvent.PayloadJson
        });
    }

    private AutoTicketCreateTicketRequest BuildTicketRequest(AgentMonitoringEvent monitoringEvent, AutoTicketRule rule, IReadOnlyCollection<string> labels)
    {
        var priority = rule.TargetPriority ?? monitoringEvent.Severity switch
        {
            MonitoringEventSeverity.Critical => TicketPriority.Critical,
            MonitoringEventSeverity.Warning => TicketPriority.High,
            _ => TicketPriority.Low
        };

        return new AutoTicketCreateTicketRequest
        {
            ClientId = monitoringEvent.ClientId,
            SiteId = monitoringEvent.SiteId,
            AgentId = monitoringEvent.AgentId,
            DepartmentId = rule.TargetDepartmentId,
            WorkflowProfileId = rule.TargetWorkflowProfileId,
            Category = rule.TargetCategory ?? "Alert",
            Priority = priority,
            Title = string.IsNullOrWhiteSpace(monitoringEvent.Title)
                ? $"[AutoTicket] {monitoringEvent.AlertCode}"
                : monitoringEvent.Title,
            Description = BuildDescription(monitoringEvent, labels),
            ActivityMessage = $"Ticket created automatically from monitoring event {monitoringEvent.Id}: {monitoringEvent.AlertCode}"
        };
    }

    private static string BuildDescription(AgentMonitoringEvent monitoringEvent, IReadOnlyCollection<string> labels)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(monitoringEvent.Message))
        {
            builder.AppendLine(monitoringEvent.Message);
            builder.AppendLine();
        }

        builder.AppendLine($"AlertCode: {monitoringEvent.AlertCode}");
        builder.AppendLine($"Severity: {monitoringEvent.Severity}");
        builder.AppendLine($"Source: {monitoringEvent.Source}");
        builder.AppendLine($"OccurredAt: {monitoringEvent.OccurredAt:O}");

        if (!string.IsNullOrWhiteSpace(monitoringEvent.MetricKey))
            builder.AppendLine($"Metric: {monitoringEvent.MetricKey}");

        if (monitoringEvent.MetricValue.HasValue)
            builder.AppendLine($"MetricValue: {monitoringEvent.MetricValue.Value}");

        if (labels.Count > 0)
            builder.AppendLine($"Labels: {string.Join(", ", labels)}");

        if (!string.IsNullOrWhiteSpace(monitoringEvent.PayloadJson))
        {
            builder.AppendLine();
            builder.AppendLine("Payload:");
            builder.AppendLine(monitoringEvent.PayloadJson);
        }

        return builder.ToString().Trim();
    }

    private bool CanCreateTicketsFor(Guid clientId, Guid? siteId)
    {
        if (_options.ShadowMode)
            return false;

        var canaryClientIds = ParseGuidSet(_options.CanaryClientIds);
        var canarySiteIds = ParseGuidSet(_options.CanarySiteIds);

        if (canaryClientIds.Count == 0 && canarySiteIds.Count == 0)
            return true;

        return canaryClientIds.Contains(clientId)
            || (siteId.HasValue && canarySiteIds.Contains(siteId.Value));
    }

    private static HashSet<Guid> ParseGuidSet(IEnumerable<string>? values)
    {
        var result = new HashSet<Guid>();
        if (values is null)
            return result;

        foreach (var value in values)
        {
            if (Guid.TryParse(value, out var guid))
                result.Add(guid);
        }

        return result;
    }
}