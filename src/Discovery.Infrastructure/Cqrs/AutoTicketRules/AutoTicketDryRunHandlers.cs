using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.AutoTicketRules.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Cqrs.AutoTicketRules;

/// <summary>
/// Dry-run / simulate: responde "o que aconteceria" SEM gravar nada.
///
/// Resolve a política hierárquica, roda o motor de regras e o avaliador de
/// sinal, e devolve o veredito + a chave de dedup. É o instrumento que permite
/// validar a configuração antes de ligar a criação de chamados.
/// </summary>
public sealed class AutoTicketRuleDryRunQueryHandler(
    IAutoTicketRuleEngineService ruleEngine,
    IAutoTicketRuleRepository ruleRepository,
    IAutoTicketPolicyResolver policyResolver,
    IAutoTicketSignalEvaluator signalEvaluator,
    IDedupFingerprintService dedupFingerprint,
    DiscoveryDbContext db) : IRequestHandler<AutoTicketRuleDryRunQuery, Result<AutoTicketRuleDryRunResponse>>
{
    public async Task<Result<AutoTicketRuleDryRunResponse>> Handle(AutoTicketRuleDryRunQuery q, CancellationToken ct)
    {
        var request = q.Request;
        if (request.ClientId == Guid.Empty)
            return Result<AutoTicketRuleDryRunResponse>.Failure(Error.Validation("clientId", "clientId is required."));

        // O avaliador de sinal carrega a definição do sensor por SourceRefId para
        // aplicar a política POR SENSOR (Never/OnEscalation/Always). Sem isso, a
        // simulação ignoraria o vínculo do sensor e diria "criaria" onde a
        // produção bloqueia — o pior tipo de divergência num dry-run, porque dá
        // falsa confiança ao operador.
        var sensorKey = ExtractSensorKey(request.AlertCode);
        var sensorId = await ResolveSensorIdAsync(sensorKey, request.AgentId, ct);

        var monitoringEvent = new AgentMonitoringEvent
        {
            Id = Guid.NewGuid(),
            ClientId = request.ClientId,
            SiteId = request.SiteId,
            AgentId = request.AgentId,
            AlertCode = request.AlertCode,
            Severity = request.Severity,
            Title = request.Title ?? string.Empty,
            Message = request.Message ?? string.Empty,
            MetricKey = request.MetricKey,
            MetricValue = request.MetricValue,
            PayloadJson = request.PayloadJson,
            Source = request.Source,
            SourceRefId = sensorId,
            OccurredAt = request.OccurredAt ?? DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        var labels = request.Labels?.ToList() ?? [];

        // Regra: quando um id é informado, restringe o candidato àquela regra.
        IReadOnlyCollection<Discovery.Core.Entities.AutoTicketRule>? candidates = null;
        if (q.RuleId.HasValue)
        {
            var rule = await ruleRepository.GetByIdAsync(q.RuleId.Value);
            if (rule is null)
                return Result<AutoTicketRuleDryRunResponse>.Failure(Error.NotFound($"Rule {q.RuleId} not found."));

            candidates = [rule];
        }

        var decision = await ruleEngine.EvaluateAsync(monitoringEvent, labels, candidates, ct);

        var policy = await policyResolver.ResolveAsync(request.ClientId, request.SiteId, ct);

        var verdict = await signalEvaluator.EvaluateAsync(monitoringEvent, policy, sensorKey, ct);

        // O que aconteceria, considerando política + regra + sinal.
        var wouldCreate = policy.Enabled
                          && policy.Mode != AutoTicketScopeMode.Shadow
                          && policy.IsInCanaryScope(request.ClientId, request.SiteId)
                          && decision.ShouldCreateTicket
                          && verdict.ShouldCreate;

        var reason = BuildReason(policy, decision, verdict, request.ClientId, request.SiteId);

        var dedupKey = decision.Rule is not null
            ? dedupFingerprint.BuildDedupKey(monitoringEvent, decision.Rule)
            : null;

        return Result<AutoTicketRuleDryRunResponse>.Success(new AutoTicketRuleDryRunResponse
        {
            Decision = wouldCreate ? AutoTicketDecision.Created : AutoTicketDecision.MatchedNoAction,
            WouldCreateTicket = wouldCreate,
            RuleId = decision.Rule?.Id,
            RuleName = decision.Rule?.Name,
            Reason = reason,
            DedupKey = dedupKey
        });
    }

    /// <summary>
    /// Explica o resultado na MESMA ordem em que <c>wouldCreate</c> decide.
    ///
    /// Manter as duas em sincronia é essencial: se o motivo não cobrir um veto
    /// que o resultado aplica, a simulação diz "criaria" e devolve
    /// <c>WouldCreateTicket = false</c> — contradição que destrói a confiança na
    /// ferramenta, que existe justamente para explicar.
    /// </summary>
    private static string BuildReason(
        ResolvedAutoTicketPolicy policy,
        AutoTicketRuleDecision decision,
        AutoTicketSignalVerdict verdict,
        Guid clientId,
        Guid? siteId)
    {
        if (!policy.Enabled)
            return policy.DisabledByLevel is null
                ? "AutoTicket is disabled by configuration."
                : $"AutoTicket is disabled at {policy.DisabledByLevel} level.";

        if (policy.Mode == AutoTicketScopeMode.Shadow)
            return "AutoTicket shadow mode is active (would not create a ticket).";

        // O canário precisa ser explicado ANTES da regra e do sinal: é o veto
        // mais externo, e era o único ausente daqui.
        if (!policy.IsInCanaryScope(clientId, siteId))
            return "Monitoring event is outside of the configured canary scope.";

        if (!decision.ShouldCreateTicket)
            return decision.Reason;

        if (!verdict.ShouldCreate)
            return $"Signal verdict: {verdict.Decision} — {verdict.Reason}";

        return $"Would create ticket via rule '{decision.Rule?.Name}' (signal {verdict.Decision}, score {verdict.Score}).";
    }

    internal static string? ExtractSensorKey(string? alertCode)
    {
        const string prefix = "sensor:";
        if (string.IsNullOrWhiteSpace(alertCode)) return null;
        if (!alertCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var key = alertCode[prefix.Length..].Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>
    /// Descobre o Id da definição do sensor a partir da chave, para que a
    /// simulação aplique a política POR SENSOR exatamente como a produção faz.
    /// Devolve null quando não há chave ou a definição não existe — nesse caso o
    /// avaliador simplesmente não aplica política de sensor (mesmo comportamento
    /// de um evento sem SourceRefId).
    /// </summary>
    private async Task<Guid?> ResolveSensorIdAsync(
        string? sensorKey, Guid agentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sensorKey)) return null;

        return await db.SensorDefinitions.AsNoTracking()
            .Where(s => s.Key == sensorKey)
            .OrderByDescending(s => s.AgentId == agentId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
    }
}

/// <summary>
/// Estatísticas de decisão de uma regra. Reaproveita GetRuleStatsAsync, que já
/// existia no repositório mas nunca era exposto.
/// </summary>
public sealed class AutoTicketRuleStatsQueryHandler(
    IAutoTicketRuleRepository ruleRepository,
    IAutoTicketRuleExecutionRepository executionRepository)
    : IRequestHandler<AutoTicketRuleStatsQuery, Result<AutoTicketRuleStatsResponse>>
{
    public async Task<Result<AutoTicketRuleStatsResponse>> Handle(AutoTicketRuleStatsQuery q, CancellationToken ct)
    {
        var rule = await ruleRepository.GetByIdAsync(q.RuleId);
        if (rule is null)
            return Result<AutoTicketRuleStatsResponse>.Failure(Error.NotFound($"Rule {q.RuleId} not found."));

        var periodHours = q.PeriodHours <= 0 ? 168 : q.PeriodHours;
        var periodEnd = DateTime.UtcNow;
        var periodStart = periodEnd.AddHours(-periodHours);

        var snapshot = await executionRepository.GetRuleStatsAsync(rule, periodStart, periodEnd);

        var total = Math.Max(1, snapshot.SelectedExecutions);

        return Result<AutoTicketRuleStatsResponse>.Success(new AutoTicketRuleStatsResponse
        {
            RuleId = rule.Id,
            RuleName = rule.Name,
            ScopeLevel = rule.ScopeLevel,
            ScopeId = rule.ScopeId,
            AlertCodeFilter = rule.AlertCodeFilter,
            PeriodHours = periodHours,
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            TotalEvaluations = snapshot.TotalEvaluations,
            SelectedExecutions = snapshot.SelectedExecutions,
            CreatedCount = snapshot.CreatedCount,
            DedupedCount = snapshot.DedupedCount,
            SuppressedCount = snapshot.SuppressedCount,
            MatchedNoActionCount = snapshot.MatchedNoActionCount,
            FailedCount = snapshot.FailedCount,
            RateLimitedCount = snapshot.RateLimitedCount,
            MatchRate = (double)snapshot.SelectedExecutions / total,
            DedupRate = (double)snapshot.DedupedCount / total,
            CreateRate = (double)snapshot.CreatedCount / total,
            FailureRate = (double)snapshot.FailedCount / total,
            FirstSelectedAtUtc = snapshot.FirstSelectedAtUtc,
            LastSelectedAtUtc = snapshot.LastSelectedAtUtc
        });
    }
}
