using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
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
    private static readonly Counter<long> SignalSuppressedCounter = Meter.CreateCounter<long>("auto_ticket_signal_suppressed_total");
    private static readonly Counter<long> SignalHeldCounter = Meter.CreateCounter<long>("auto_ticket_held_total");
    private static readonly Counter<long> DisabledByLevelCounter = Meter.CreateCounter<long>("auto_ticket_disabled_by_level_total");
    private static readonly Counter<long> AiSuggestionAppliedCounter = Meter.CreateCounter<long>("auto_ticket_ai_suggestion_applied_total");
    private static readonly Counter<long> AiSuggestionRejectedCounter = Meter.CreateCounter<long>("auto_ticket_ai_suggestion_rejected_total");

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
    private readonly IAutoTicketPolicyResolver _policyResolver;
    private readonly IAutoTicketSignalEvaluator _signalEvaluator;
    private readonly IAutoTicketEnrichmentService _enrichmentService;
    private readonly IAutoTicketSuggestionApplier _suggestionApplier;
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
        IAutoTicketPolicyResolver policyResolver,
        IAutoTicketSignalEvaluator signalEvaluator,
        IAutoTicketEnrichmentService enrichmentService,
        IAutoTicketSuggestionApplier suggestionApplier,
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
        _policyResolver = policyResolver;
        _signalEvaluator = signalEvaluator;
        _enrichmentService = enrichmentService;
        _suggestionApplier = suggestionApplier;
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

            // Step 2: Resolve a política hierárquica (Server -> Client -> Site).
            // A partir daqui a decisão vem da política resolvida, e não mais do
            // IOptions lido direto — o appsettings passa a ser apenas o default
            // de bootstrap do nível global.
            var policy = await _policyResolver.ResolveAsync(
                monitoringEvent.ClientId, monitoringEvent.SiteId, cancellationToken);

            var (disabled, disabledResult) = await HandleConfigAndScopeCheckAsync(monitoringEvent, decision, policy);
            if (disabled) return disabledResult!;

            // Step 3: Avaliação de SINAL (determinística). Decide se o evento
            // merece um chamado: vínculo de sensor, resolução, manutenção, host
            // não alcançável, flapping e persistência (magnitude × duração).
            var sensorKey = ExtractSensorKey(monitoringEvent.AlertCode);
            var verdict = await _signalEvaluator.EvaluateAsync(monitoringEvent, policy, sensorKey, cancellationToken);

            var (signalHandled, signalResult) = await HandleSignalVerdictAsync(monitoringEvent, decision, verdict);
            if (signalHandled) return signalResult!;

            // Step 4: Dedup + Reopen + Rate limit + Enriquecimento + Create
            var dedupKey = _dedupFingerprintService.BuildDedupKey(monitoringEvent, decision.Rule!);
            execution = await ExecuteCreatePipelineAsync(
                monitoringEvent, decision, labels, dedupKey, policy, verdict, cancellationToken);
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

    /// <summary>
    /// Traduz o veredito do sinal para as decisões JÁ existentes de
    /// AutoTicketDecision (nenhum valor novo de enum) e persiste a evidência.
    /// </summary>
    private async Task<(bool Handled, AutoTicketRuleExecution? Result)> HandleSignalVerdictAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        AutoTicketSignalVerdict verdict)
    {
        if (verdict.Decision == AutoTicketSignalDecision.Suppress)
        {
            SignalSuppressedCounter.Add(1,
                new KeyValuePair<string, object?>("suppression_source", verdict.SuppressionSource.ToString()));

            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Suppressed,
                verdict.Reason, null, null, false, verdict);
            return (true, ex);
        }

        if (verdict.Decision == AutoTicketSignalDecision.Hold)
        {
            SignalHeldCounter.Add(1);

            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.MatchedNoAction,
                verdict.Reason, null, null, false, verdict);
            return (true, ex);
        }

        return (false, null);
    }

    /// <summary>
    /// Extrai a chave do sensor de um AlertCode "sensor:&lt;key&gt;".
    /// Retorna null para eventos que não vieram de sensor.
    /// </summary>
    internal static string? ExtractSensorKey(string? alertCode)
    {
        const string prefix = "sensor:";
        if (string.IsNullOrWhiteSpace(alertCode)) return null;
        if (!alertCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var key = alertCode[prefix.Length..].Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    private async Task<(bool Disabled, AutoTicketRuleExecution? Result)> HandleConfigAndScopeCheckAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        ResolvedAutoTicketPolicy policy)
    {
        if (!policy.Enabled)
        {
            DisabledByLevelCounter.Add(1, new KeyValuePair<string, object?>("level", policy.DisabledByLevel ?? "Server"));

            var reason = policy.DisabledByLevel is null
                ? "AutoTicket is disabled by configuration."
                : $"AutoTicket is disabled at {policy.DisabledByLevel} level.";

            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.MatchedNoAction,
                reason, null, null, false);
            return (true, ex);
        }

        if (policy.Mode == AutoTicketScopeMode.Shadow)
        {
            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.MatchedNoAction,
                "AutoTicket shadow mode is active.", null, null, false);
            return (true, ex);
        }

        if (!policy.IsInCanaryScope(monitoringEvent.ClientId, monitoringEvent.SiteId))
        {
            var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.MatchedNoAction,
                "Monitoring event is outside of the configured canary scope.", null, null, false);
            return (true, ex);
        }

        return (false, null);
    }

    private async Task<AutoTicketRuleExecution> ExecuteCreatePipelineAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleDecision decision,
        IReadOnlyCollection<string> labels,
        string dedupKey,
        ResolvedAutoTicketPolicy policy,
        AutoTicketSignalVerdict verdict,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Dedup via correlation lock. A janela vem da política quando ela define
        // explicitamente (herdada do global, sobrescrita por cliente/site);
        // caso contrário, vale a janela da própria regra.
        var dedupWindowMinutes = policy.DedupWindowMinutes > 0
            ? policy.DedupWindowMinutes
            : decision.Rule!.DedupWindowMinutes;

        var dedupResult = await _dedupService.TryAcquireOrGetAsync(
            dedupKey, TimeSpan.FromMinutes(Math.Max(1, dedupWindowMinutes)), ct);

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
        var (reopened, reopenResult) = await TryReopenClosedTicketAsync(
            monitoringEvent, decision, ticketRequest, dedupKey, policy, ct);
        if (reopened)
        {
            await transaction.CommitAsync(ct);
            return reopenResult!;
        }

        // Rate limit check
        var (rateLimited, rateLimitResult) = await CheckRateLimitAsync(monitoringEvent, decision, dedupKey, policy, ct);
        if (rateLimited)
        {
            await transaction.CommitAsync(ct);
            return rateLimitResult!;
        }

        // Enriquecimento por IA (Fase 5). A IA NÃO decide abrir — isso já foi
        // decidido pelo avaliador de sinal. Aqui ela apenas redige o conteúdo e
        // SUGERE prioridade/departamento/categoria. Qualquer falha degrada para
        // o texto determinístico e o chamado é criado do mesmo jeito.
        AutoTicketEnrichment? enrichment = null;
        try
        {
            enrichment = await _enrichmentService.EnrichAsync(monitoringEvent, policy, ct);
        }
        catch (Exception ex)
        {
            // Defesa em profundidade: o serviço já não deve lançar, mas a criação
            // do chamado jamais pode depender da IA.
            _logger.LogWarning(ex,
                "Enriquecimento por IA falhou para o evento {MonitoringEventId}; seguindo com o texto determinístico.",
                monitoringEvent.Id);
        }

        // Aplicação por política (Suggest vs AutoApply), com piso de prioridade.
        AutoTicketAppliedSuggestion? applied = null;
        if (enrichment is not null && !enrichment.UsedFallback)
        {
            applied = await _suggestionApplier.ApplyAsync(
                monitoringEvent, policy, enrichment, ticketRequest.Priority, verdict.ShouldEscalate, ct);

            // AutoTicketCreateTicketRequest é classe (não record), então os
            // valores finais são compostos explicitamente.
            //
            // PRECEDÊNCIA (do mais específico para o mais genérico):
            //   1. sugestão que a IA efetivamente APLICOU
            //   2. valor definido pela REGRA (mais específico que o default)
            //   3. default da POLÍTICA hierárquica
            //
            // applied.DepartmentId/Category vêm nulos quando a IA não aplicou —
            // justamente para não mascarar o valor da regra.
            ticketRequest = new AutoTicketCreateTicketRequest
            {
                ClientId = ticketRequest.ClientId,
                SiteId = ticketRequest.SiteId,
                AgentId = ticketRequest.AgentId,
                DepartmentId = applied.DepartmentId
                    ?? ticketRequest.DepartmentId
                    ?? policy.DefaultDepartmentId,
                WorkflowProfileId = ticketRequest.WorkflowProfileId,
                Title = !string.IsNullOrWhiteSpace(applied.Title) ? applied.Title! : ticketRequest.Title,
                Description = !string.IsNullOrWhiteSpace(applied.Description) ? applied.Description! : ticketRequest.Description,
                Category = !string.IsNullOrWhiteSpace(applied.Category)
                    ? applied.Category
                    : !string.IsNullOrWhiteSpace(ticketRequest.Category)
                        ? ticketRequest.Category
                        : policy.DefaultCategory,
                Priority = applied.Priority,
                ActivityMessage = ticketRequest.ActivityMessage
            };
        }

        // Create ticket
        var ticket = await _alertToTicketService.CreateTicketFromMonitoringEventAsync(ticketRequest, ct);
        await _dedupService.RegisterCreatedTicketAsync(dedupKey, ticket.Id, ct);

        var execution = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Created,
            decision.Reason, ticket.Id, dedupKey, false, verdict);

        // Auditoria das sugestões: guarda aplicadas E não aplicadas. É o insumo
        // para medir a taxa de override antes de dar mais autonomia à IA.
        if (enrichment is not null)
        {
            await PersistSuggestionsAsync(monitoringEvent, execution, ticket.Id, policy, enrichment, applied, ct);
        }

        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "AutoTicket created ticket {TicketId} for monitoring event {MonitoringEventId} using rule {RuleId} and dedupKey {DedupKey}.",
            ticket.Id, monitoringEvent.Id, decision.Rule.Id, LogSanitizer.Sanitize(dedupKey));

        return execution;
    }

    /// <summary>
    /// Grava o livro de sugestões. Guardar também as NÃO aplicadas é o que
    /// permite avaliar se a IA acerta — insumo para decidir se vale ir de
    /// Suggest para AutoApply.
    /// </summary>
    private async Task PersistSuggestionsAsync(
        AgentMonitoringEvent monitoringEvent,
        AutoTicketRuleExecution execution,
        Guid ticketId,
        ResolvedAutoTicketPolicy policy,
        AutoTicketEnrichment enrichment,
        AutoTicketAppliedSuggestion? applied,
        CancellationToken ct)
    {
        var items = applied?.Suggestions ?? enrichment.Suggestions;
        if (items.Count == 0) return;

        var source = enrichment.UsedFallback ? AutoTicketSuggestionSource.Fallback : AutoTicketSuggestionSource.Ai;

        foreach (var item in items)
        {
            _db.AutoTicketSuggestions.Add(new AutoTicketSuggestion
            {
                Id = Guid.NewGuid(),
                RuleExecutionId = execution.Id,
                MonitoringEventId = monitoringEvent.Id,
                TicketId = ticketId,
                AgentId = monitoringEvent.AgentId,
                Field = item.Field,
                SuggestedValue = item.SuggestedValue,
                AppliedValue = item.AppliedValue,
                Confidence = item.Confidence,
                Mode = (int)policy.AiMode,
                StrategySource = source,
                Rationale = item.Rationale,
                Model = enrichment.Model,
                TokensUsed = enrichment.TokensUsed,
                Applied = item.Applied,
                NotAppliedReason = item.NotAppliedReason,
                CreatedAt = DateTime.UtcNow
            });
        }

        AiSuggestionAppliedCounter.Add(items.Count(i => i.Applied));
        AiSuggestionRejectedCounter.Add(items.Count(i => !i.Applied));

        await _db.SaveChangesAsync(ct);
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
        ResolvedAutoTicketPolicy policy,
        CancellationToken ct)
    {
        // Janela vem da política resolvida (permite override por cliente/site).
        var reopenWindowMinutes = policy.ReopenWindowMinutes;

        if (reopenWindowMinutes <= 0)
            return (false, null);

        var reopenableClosedTicketId = await _executionRepository.GetReopenableClosedTicketIdAsync(
            monitoringEvent.ClientId, monitoringEvent.AgentId, monitoringEvent.AlertCode,
            DateTime.UtcNow.AddMinutes(-reopenWindowMinutes),
            ticketRequest.DepartmentId, ticketRequest.WorkflowProfileId, ticketRequest.Category);

        if (!reopenableClosedTicketId.HasValue)
            return (false, null);

        var reopenableTicket = await _ticketRepository.GetByIdAsync(reopenableClosedTicketId.Value);
        var initialState = await _workflowRepository.GetInitialStateAsync(monitoringEvent.ClientId);

        if (reopenableTicket is null || !reopenableTicket.ClosedAt.HasValue || initialState is null)
            return (false, null);

        var previousClosedAt = reopenableTicket.ClosedAt.Value;
        var now = DateTime.UtcNow;

        // Reabertura deve limpar os marcadores de SLA (senão o ticket reaberto
        // continua aparecendo como "SLA violado").
        reopenableTicket.WorkflowStateId = initialState.Id;
        reopenableTicket.ClosedAt = null;
        reopenableTicket.SlaBreached = false;
        reopenableTicket.SlaPausedSeconds = 0;
        reopenableTicket.SlaHoldStartedAt = null;
        // Reabertura entra no estado inicial: inicia pausa se ele pausar o SLA.
        SlaHold.ApplyStateChange(reopenableTicket, oldState: null, initialState, now);
        // Zera expirações antigas: sem isso o SlaMonitoringJob volta a marcar
        // o ticket reaberto como violado a cada 5 minutos.
        reopenableTicket.SlaExpiresAt = null;
        reopenableTicket.SlaFirstResponseExpiresAt = null;
        await _ticketRepository.UpdateAsync(reopenableTicket);
        await _activityLogService.LogActivityAsync(
            reopenableTicket.Id, TicketActivityType.Reopened, null,
            previousClosedAt.ToString("O"), null,
            $"Ticket reaberto automaticamente a partir do evento de monitoramento {monitoringEvent.Id}: {monitoringEvent.AlertCode}");
        await _dedupService.RegisterCreatedTicketAsync(dedupKey, reopenableTicket.Id, ct);

        var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.Deduped,
            $"Closed AutoTicket was reopened inside the configured reopen window ({reopenWindowMinutes} min).",
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
        ResolvedAutoTicketPolicy policy,
        CancellationToken ct)
    {
        // O limite vem da POLÍTICA RESOLVIDA, não do appsettings: é o que faz o
        // override por cliente/site valer. Um cliente pode ter teto maior, outro
        // menor, e o global continua sendo o default.
        var limitPerHour = policy.MaxCreatedTicketsPerHourPerAlertCode;

        if (limitPerHour <= 0)
            return (false, null);

        var createdInLastHour = await _executionRepository.GetCreatedCountForClientAlertAsync(
            monitoringEvent.ClientId, monitoringEvent.AlertCode, DateTime.UtcNow.AddHours(-1));

        if (createdInLastHour < limitPerHour)
            return (false, null);

        var ex = await CreateExecutionAsync(monitoringEvent, decision.Rule, AutoTicketDecision.RateLimited,
            $"Client alert rate limit reached for '{monitoringEvent.AlertCode}' ({limitPerHour} tickets/hour).",
            null, dedupKey, false);

        _logger.LogWarning(
            "AutoTicket rate limit reached for client {ClientId}, alertCode {AlertCode}. Limit={LimitPerHour}, monitoringEventId={MonitoringEventId}.",
            monitoringEvent.ClientId, LogSanitizer.Sanitize(monitoringEvent.AlertCode),
            limitPerHour, monitoringEvent.Id);

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
        bool dedupHit,
        AutoTicketSignalVerdict? verdict = null)
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
            // O snapshots carrega o payload do evento e, quando houve avaliação
            // de sinal, o veredito + evidências. A coluna é jsonb livre — não
            // exige migration.
            PayloadSnapshotJson = BuildPayloadSnapshot(monitoringEvent.PayloadJson, verdict)
        });
    }

    /// <summary>
    /// Monta o snapshot gravado na execução: envelope com o payload original e o
    /// veredito do sinal (decisão, score, origem da supressão e evidências).
    /// </summary>
    private static string? BuildPayloadSnapshot(string? payloadJson, AutoTicketSignalVerdict? verdict)
    {
        if (verdict is null) return payloadJson;

        var envelope = new Dictionary<string, object?>
        {
            ["payload"] = payloadJson,
            ["signal"] = new Dictionary<string, object?>
            {
                ["decision"] = verdict.Decision.ToString(),
                ["score"] = verdict.Score,
                ["impact"] = verdict.Impact.ToString(),
                ["suppressionSource"] = verdict.SuppressionSource.ToString(),
                ["reason"] = verdict.Reason,
                ["evidence"] = verdict.Evidence
            }
        };

        return JsonSerializer.Serialize(envelope);
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

    // Nota: CanCreateTicketsFor/ParseGuidSet foram REMOVIDOS. Eles liam o
    // appsettings diretamente (ShadowMode/canary) e ficaram sem chamador quando
    // a decisão passou a vir da política resolvida (ResolvedAutoTicketPolicy),
    // que já aplica shadow, canário e override por cliente/site. Manter código
    // morto lendo configuração antiga é uma armadilha para quem for mexer depois.
}