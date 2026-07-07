using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
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

            // Step 1: No match / suppressed / not-creating
            var (handled, earlyResult) = await HandleNonCreateDecisionsAsync(monitoringEvent, decision, cancellationToken);
            if (handled) return earlyResult!;

            // Step 2: Check if enabled and in scope
            var (disabled, disabledResult) = HandleConfigAndScopeCheck(monitoringEvent, decision);
            if (disabled) return disabledResult!;

            // Step 3: Dedup + Reopen + Rate limit + Create
            var dedupKey = _dedupFingerprintService.BuildDedupKey(monitoringEvent, decision.Rule!);
            execution = await ExecuteCreatePipelineAsync(monitoringEvent, decision, labels, dedupKey, cancellationToken);
            return execution;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AutoTicket failed for monitoring event {MonitoringEventId} ({AlertCode}).", monitoringEvent.Id, LogSanitizer.Sanitize(monitoringEvent.AlertCode));
            execution = await CreateExecutionAsync(
                monitoringEvent, matchedRule, AutoTicketDecision.Failed, ex.Message, null, null, false);
            return execution;
        }
        finally
        {
            stopwatch.Stop();
            RecordMetrics(monitoringEvent, execution, stopwatch);
        }
    }

    private async Task<(bool Handled, AutoTicketRuleExecution? Result)> HandleNonCreateDecisionsAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        CancellationToken ct)
    {
        if (!decision.Matched)
        {
            var ex = await CreateExecutionAsync(monitoringEvent, null, AutoTicketDecision.MatchedNoAction, decision.Reason, null, null, false);
            return (true, ex);
        }

        if (decision.IsSuppressed)
        {
            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Suppressed, decision.Reason, null, null, false);
            return (true, ex);
        }

        if (!decision.ShouldCreateTicket)
        {
            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.MatchedNoAction, decision.Reason, null, null, false);
            return (true, ex);
        }

        return (false, null);
    }

    private (bool Disabled, AutoTicketRuleExecution? Result) HandleConfigAndScopeCheck(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision)
    {
        if (!_options.Enabled)
        {
            var ex = CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.MatchedNoAction,
                "AutoTicket is disabled by configuration.", null, null, false).Result;
            return (true, ex);
        }

        if (!CanCreateTicketsFor(monitoringEvent.ClientId, monitoringEvent.SiteId))
        {
            var ex = CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.MatchedNoAction,
                _options.ShadowMode ? "AutoTicket shadow mode is active." : "Monitoring event is outside of the configured canary scope.",
                null, null, false).Result;
            return (true, ex);
        }

        return (false, null);
    }

    private async Task<AutoTicketRuleExecution> ExecuteCreatePipelineAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        IReadOnlyCollection<string> labels,
        string dedupKey,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Dedup via correlation lock
        var dedupResult = await _dedupService.TryAcquireOrGetAsync(
            dedupKey, TimeSpan.FromMinutes(Math.Max(1, decision.Rule!.DedupWindowMinutes)), ct);

        if (!dedupResult.Acquired)
        {
            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Deduped,
                "Existing correlation lock found inside dedup window.", dedupResult.ExistingTicketId, dedupKey, true);
            await transaction.CommitAsync(ct);
            return ex;
        }

        var ticketRequest = BuildTicketRequest(monitoringEvent, decision.Rule, labels);

        // Check reusable open ticket
        var (reused, reuseResult) = await TryReuseOpenTicketAsync(monitoringEvent, decision, ticketRequest, dedupKey, ct);
        if (reused)
        {
            await transaction.CommitAsync(ct);
            return reuseResult!;
        }

        // Check reopenable closed ticket
        var (reopened, reopenResult) = await TryReopenClosedTicketAsync(monitoringEvent, decision, ticketRequest, dedupKey, ct);
        if (reopened)
        {
            await transaction.CommitAsync(ct);
            return reopenResult!;
        }

        // Rate limit check
        var (rateLimited, rateLimitResult) = await CheckRateLimitAsync(monitoringEvent, decision, dedupKey, ct);
        if (rateLimited)
        {
            await transaction.CommitAsync(ct);
            return rateLimitResult!;
        }

        // Create ticket
        var ticket = await _alertToTicketService.CreateTicketFromMonitoringEventAsync(ticketRequest, ct);
        await _dedupService.RegisterCreatedTicketAsync(dedupKey, ticket.Id, ct);

        var execution = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Created,
            decision.Reason, ticket.Id, dedupKey, false);

        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "AutoTicket created ticket {TicketId} for monitoring event {MonitoringEventId} using rule {RuleId} and dedupKey {DedupKey}.",
            ticket.Id, monitoringEvent.Id, decision.Rule.Id, LogSanitizer.Sanitize(dedupKey));

        return execution;
    }

    private async Task<(bool Reused, AutoTicketRuleExecution? Result)> TryReuseOpenTicketAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        AutoTicketCreateTicketRequest ticketRequest,
        string dedupKey,
        CancellationToken ct)
    {
        var reusableOpenTicketId = await _executionRepository.GetReusableOpenTicketIdAsync(
            monitoringEvent.ClientId, monitoringEvent.AgentId, monitoringEvent.AlertCode,
            ticketRequest.DepartmentId, ticketRequest.WorkflowProfileId, ticketRequest.Category);

        if (!reusableOpenTicketId.HasValue)
            return (false, null);

        await _dedupService.RegisterCreatedTicketAsync(dedupKey, reusableOpenTicketId.Value, ct);

        var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Deduped,
            "Existing open AutoTicket was found for the same agent and alert type.",
            reusableOpenTicketId.Value, dedupKey, true);

        _logger.LogInformation(
            "AutoTicket reused existing open ticket {TicketId} for monitoring event {MonitoringEventId} using dedupKey {DedupKey}.",
            reusableOpenTicketId.Value, monitoringEvent.Id, LogSanitizer.Sanitize(dedupKey));

        return (true, ex);
    }

    private async Task<(bool Reopened, AutoTicketRuleExecution? Result)> TryReopenClosedTicketAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        AutoTicketCreateTicketRequest ticketRequest,
        string dedupKey,
        CancellationToken ct)
    {
        if (_options.ReopenWindowMinutes <= 0)
            return (false, null);

        var reopenableClosedTicketId = await _executionRepository.GetReopenableClosedTicketIdAsync(
            monitoringEvent.ClientId, monitoringEvent.AgentId, monitoringEvent.AlertCode,
            DateTime.UtcNow.AddMinutes(-_options.ReopenWindowMinutes),
            ticketRequest.DepartmentId, ticketRequest.WorkflowProfileId, ticketRequest.Category);

        if (!reopenableClosedTicketId.HasValue)
            return (false, null);

        var reopenableTicket = await _ticketRepository.GetByIdAsync(reopenableClosedTicketId.Value);
        var initialState = await _workflowRepository.GetInitialStateAsync(monitoringEvent.ClientId);

        if (reopenableTicket is null || !reopenableTicket.ClosedAt.HasValue || initialState is null)
            return (false, null);

        var previousClosedAt = reopenableTicket.ClosedAt.Value;
        await _ticketRepository.UpdateWorkflowStateAsync(reopenableTicket.Id, initialState.Id, closedAt: null);
        await _activityLogService.LogActivityAsync(
            reopenableTicket.Id, TicketActivityType.Reopened, null,
            previousClosedAt.ToString("O"), null,
            $"Ticket reaberto automaticamente a partir do evento de monitoramento {monitoringEvent.Id}: {monitoringEvent.AlertCode}");
        await _dedupService.RegisterCreatedTicketAsync(dedupKey, reopenableTicket.Id, ct);

        var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Deduped,
            $"Closed AutoTicket was reopened inside the configured reopen window ({_options.ReopenWindowMinutes} min).",
            reopenableTicket.Id, dedupKey, true);

        _logger.LogInformation(
            "AutoTicket reopened closed ticket {TicketId} for monitoring event {MonitoringEventId} using dedupKey {DedupKey}.",
            reopenableTicket.Id, monitoringEvent.Id, LogSanitizer.Sanitize(dedupKey));

        return (true, ex);
    }

    private async Task<(bool RateLimited, AutoTicketRuleExecution? Result)> CheckRateLimitAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        string dedupKey,
        CancellationToken ct)
    {
        if (_options.MaxCreatedTicketsPerHourPerAlertCode <= 0)
            return (false, null);

        var createdInLastHour = await _executionRepository.GetCreatedCountForClientAlertAsync(
            monitoringEvent.ClientId, monitoringEvent.AlertCode, DateTime.UtcNow.AddHours(-1));

        if (createdInLastHour < _options.MaxCreatedTicketsPerHourPerAlertCode)
            return (false, null);

        var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.RateLimited,
            $"Client alert rate limit reached for '{monitoringEvent.AlertCode}' ({_options.MaxCreatedTicketsPerHourPerAlertCode} tickets/hour).",
            null, dedupKey, false);

        _logger.LogWarning(
            "AutoTicket rate limit reached for client {ClientId}, alertCode {AlertCode}. Limit={LimitPerHour}, monitoringEventId={MonitoringEventId}.",
            monitoringEvent.ClientId, LogSanitizer.Sanitize(monitoringEvent.AlertCode),
            _options.MaxCreatedTicketsPerHourPerAlertCode, monitoringEvent.Id);

        return (true, ex);
    }

    private void RecordMetrics(AgentMonitoringEvent monitoringEvent, AutoTicketRuleExecution? execution, Stopwatch stopwatch)
    {
        var tags = new TagList
        {
            { "decision", execution?.Decision.ToString() ?? "Unknown" },
            { "alertCode", monitoringEvent.AlertCode }
        };

        EvaluatedCounter.Add(1, tags);
        EvalDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds, tags);

        switch (execution?.Decision)
        {
            case AutoTicketDecision.Created: CreatedCounter.Add(1, tags); break;
            case AutoTicketDecision.Deduped: DedupedCounter.Add(1, tags); break;
            case AutoTicketDecision.Failed: FailedCounter.Add(1, tags); break;
            case AutoTicketDecision.RateLimited: RateLimitedCounter.Add(1, tags); break;
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