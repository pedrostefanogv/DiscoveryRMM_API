using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Avaliador determinístico do sinal de auto-ticket.
///
/// Ordem de decisão (curto-circuita no primeiro veto):
///  1. vínculo de sensor (Never / allowlist)      -> NotActionable
///  2. resolução (transition = resolved)          -> Resolved
///  3. manutenção (agente e janela)               -> Maintenance
///  4. host/agente não alcançável                 -> Dependency
///  5. flapping                                   -> Flapping
///  6. persistência (falhas + duração)            -> Hold
///  7. acionabilidade (impacto/severidade)        -> NotActionable
///  8. senão                                      -> Open / Escalate
///
/// Espelha práticas verificadas de mercado: o Checkmk só notifica serviços se o
/// host está UP; o Alertmanager descarta o transitório esperando antes de
/// notificar; o Zabbix congela dependentes enquanto o pai está em PROBLEM.
/// </summary>
public class AutoTicketSignalEvaluator(
    DiscoveryDbContext db,
    ILogger<AutoTicketSignalEvaluator> logger) : IAutoTicketSignalEvaluator
{
    public async Task<AutoTicketSignalVerdict> EvaluateAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        string? sensorKey = null,
        CancellationToken cancellationToken = default)
    {
        var evidence = new Dictionary<string, object?>
        {
            ["sensorKey"] = sensorKey,
            ["alertCode"] = monitoringEvent.AlertCode,
            ["severity"] = monitoringEvent.Severity.ToString()
        };

        // ── 1. Vínculo de sensor ──────────────────────────────────────────
        // Precedência: NEVER do sensor (mais específico) > NeverTicketSensorKeys
        // (lista da política) > AllowedSensorKeys. O mais específico vence.
        var sensorDef = await LoadSensorDefinitionAsync(monitoringEvent, cancellationToken);

        if (sensorDef is not null)
        {
            evidence["sensorPolicy"] = sensorDef.AutoTicketPolicy.ToString();
            evidence["sensorImpact"] = sensorDef.AutoTicketImpact.ToString();

            if (sensorDef.AutoTicketPolicy == SensorAutoTicketPolicy.Never)
            {
                return Suppress(AutoTicketSuppressionSource.NotActionable,
                    $"Sensor '{sensorDef.Key ?? sensorDef.Name}' is set to never open a ticket.", evidence);
            }
        }

        if (!string.IsNullOrWhiteSpace(sensorKey))
        {
            var never = policy.NeverTicketSensorKeys
                .Any(k => string.Equals(k, sensorKey, StringComparison.OrdinalIgnoreCase));
            if (never)
            {
                evidence["sensorPolicy"] = "Never";
                return Suppress(AutoTicketSuppressionSource.NotActionable,
                    $"Sensor '{sensorKey}' is configured to never open a ticket.", evidence);
            }

            if (policy.AllowedSensorKeys.Length > 0)
            {
                var allowed = policy.AllowedSensorKeys
                    .Any(k => string.Equals(k, sensorKey, StringComparison.OrdinalIgnoreCase));
                if (!allowed)
                {
                    evidence["sensorPolicy"] = "NotInAllowlist";
                    return Suppress(AutoTicketSuppressionSource.NotActionable,
                        $"Sensor '{sensorKey}' is not in the allowed sensor list.", evidence);
                }
            }
        }

        // ── 2. Evento de resolução não abre chamado ───────────────────────
        // A transição NÃO está em CorrelationId (esse campo carrega o id de
        // correlação do agente). Ela é gravada dentro do PayloadJson por
        // SensorIngestionService.BuildMonitoringEvent:
        //   { sensorId, transition, status, severity, targets, ... }
        // Ler o campo errado fazia esta guarda nunca disparar.
        var transition = ReadTransition(monitoringEvent.PayloadJson);
        evidence["transition"] = transition;

        if (string.Equals(transition, "resolved", StringComparison.OrdinalIgnoreCase))
        {
            return Suppress(AutoTicketSuppressionSource.Resolved,
                "Resolution event does not open a ticket.", evidence);
        }

        // ── 3. Manutenção ─────────────────────────────────────────────────
        var agent = await db.Agents.AsNoTracking()
            .Where(a => a.Id == monitoringEvent.AgentId)
            .Select(a => new { a.MaintenanceEnabled, a.MaintenanceReason })
            .FirstOrDefaultAsync(cancellationToken);

        if (policy.RespectAgentMaintenance && agent?.MaintenanceEnabled == true)
        {
            evidence["maintenanceActive"] = true;
            evidence["maintenanceSource"] = "Agent";
            evidence["maintenanceReason"] = agent.MaintenanceReason;
            return Suppress(AutoTicketSuppressionSource.Maintenance,
                $"Agent is in maintenance{(string.IsNullOrWhiteSpace(agent.MaintenanceReason) ? "" : $": {agent.MaintenanceReason}")}.",
                evidence);
        }

        if (policy.RespectMaintenanceWindow)
        {
            var windowBlock = await CheckMaintenanceWindowAsync(monitoringEvent, cancellationToken);
            if (windowBlock is not null)
            {
                evidence["maintenanceActive"] = true;
                evidence["maintenanceSource"] = windowBlock.Value.Source;
                return Suppress(AutoTicketSuppressionSource.Maintenance, windowBlock.Value.Reason, evidence);
            }
        }

        evidence["maintenanceActive"] = false;

        // ── 4. Host/agente não alcançável ─────────────────────────────────
        // Padrão de mercado (Checkmk/PRTG): não abrir chamado para sensores de um
        // host que não responde — o problema do host já é o chamado relevante.
        if (policy.SuppressWhenHostNotReachable)
        {
            var agentStatus = await db.Agents.AsNoTracking()
                .Where(a => a.Id == monitoringEvent.AgentId)
                .Select(a => (int?)a.Status)
                .FirstOrDefaultAsync(cancellationToken);

            // AgentStatus.Offline == 2 (ver Discovery.Core.Enums.AgentStatus).
            if (agentStatus == 2)
            {
                evidence["hostReachable"] = false;
                return Suppress(AutoTicketSuppressionSource.Dependency,
                    "Agent is offline; dependent sensor alerts are suppressed.", evidence);
            }
            evidence["hostReachable"] = true;
        }

        // ── 5. Flapping ───────────────────────────────────────────────────
        var flap = await DetectFlappingAsync(monitoringEvent, policy, sensorKey, cancellationToken);
        evidence["flapTransitions"] = flap.Transitions;
        if (flap.IsFlapping)
        {
            return Suppress(AutoTicketSuppressionSource.Flapping,
                $"Flapping detected: {flap.Transitions} transitions in {policy.FlapWindowMinutes} min.",
                evidence);
        }

        // ── 6. Persistência (magnitude × duração) ─────────────────────────
        // O sensor pode exigir uma duração mínima própria; 0 = herda a política.
        var minDurationSeconds = sensorDef?.AutoTicketMinDurationSeconds > 0
            ? sensorDef.AutoTicketMinDurationSeconds
            : policy.MinDurationSeconds;

        var persistence = await EvaluatePersistenceAsync(
            monitoringEvent, policy, minDurationSeconds, cancellationToken);
        evidence["consecutiveFailures"] = persistence.ConsecutiveFailures;
        evidence["durationSeconds"] = persistence.DurationSeconds;

        if (!persistence.IsPersistent)
        {
            return new AutoTicketSignalVerdict
            {
                Decision = AutoTicketSignalDecision.Hold,
                SuppressionSource = AutoTicketSuppressionSource.Transient,
                Reason = $"Signal not persistent: {persistence.ConsecutiveFailures}/{policy.MinConsecutiveFailures} failures, " +
                         $"{persistence.DurationSeconds}/{policy.MinDurationSeconds}s.",
                Score = 30,
                Impact = persistence.Impact,
                Evidence = evidence
            };
        }

        // ── 7. Acionabilidade ─────────────────────────────────────────────
        var severity = MapSeverity(monitoringEvent.Severity);
        var openable = severity switch
        {
            SensorSeverity.Critical => true,
            SensorSeverity.Warning => policy.OpenOnWarning,
            _ => false
        };

        if (!openable)
        {
            return Suppress(AutoTicketSuppressionSource.NotActionable,
                $"Severity '{monitoringEvent.Severity}' is not actionable for ticketing.", evidence);
        }

        // ── 8. Decisão final ──────────────────────────────────────────────
        // O impacto do sensor é um PISO: a severidade só pode elevá-lo.
        var sensorImpactFloor = sensorDef?.AutoTicketImpact ?? AutoTicketImpact.Low;
        var impact = MaxImpact(severity, persistence.Impact, policy);
        if (sensorImpactFloor > impact) impact = sensorImpactFloor;

        evidence["impact"] = impact.ToString();

        var escalate = severity == SensorSeverity.Critical
                       && impact >= policy.MinImpactToEscalate;

        return new AutoTicketSignalVerdict
        {
            Decision = escalate ? AutoTicketSignalDecision.Escalate : AutoTicketSignalDecision.Open,
            Reason = escalate
                ? "Sustained critical signal requires escalation."
                : "Sustained signal warrants a ticket.",
            Score = Math.Min(100, 50 + (persistence.ConsecutiveFailures * 10) + (escalate ? 20 : 0)),
            Impact = impact,
            Evidence = evidence
        };

        AutoTicketSignalVerdict Suppress(
            AutoTicketSuppressionSource source, string reason, Dictionary<string, object?> ev)
            => new()
            {
                Decision = AutoTicketSignalDecision.Suppress,
                SuppressionSource = source,
                Reason = reason,
                Score = 0,
                Impact = AutoTicketImpact.Low,
                Evidence = ev
            };
    }

    /// <summary>
    /// Lê a transição ("opened"/"escalated"/"resolved") do PayloadJson do evento.
    /// É onde a ingestão de sensores a grava — NÃO em CorrelationId.
    /// Devolve null quando o payload não tem a chave (evento não-sensor).
    /// </summary>
    internal static string? ReadTransition(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("transition", out var transition)
                   && transition.ValueKind == JsonValueKind.String
                ? transition.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Carrega a definição do sensor do evento (via SourceRefId). Null quando o
    /// evento não veio de um sensor — nesse caso a política hierárquica decide.
    /// </summary>
    private async Task<SensorDefinition?> LoadSensorDefinitionAsync(
        AgentMonitoringEvent monitoringEvent, CancellationToken ct)
    {
        if (monitoringEvent.SourceRefId is null) return null;

        return await db.SensorDefinitions.AsNoTracking()
            .Where(s => s.Id == monitoringEvent.SourceRefId.Value)
            .Select(s => new SensorDefinition
            {
                Id = s.Id,
                Key = s.Key,
                Name = s.Name,
                AutoTicketPolicy = s.AutoTicketPolicy,
                AutoTicketMinDurationSeconds = s.AutoTicketMinDurationSeconds,
                AutoTicketImpact = s.AutoTicketImpact
            })
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Janela de manutenção configurada em Server/Client/Site (JSON livre).
    /// Formato esperado: { enabled, daysOfWeek: [0-6], startUtc: "HH:mm", durationMinutes }.
    /// Formato inválido NÃO suprime (fail-open) e apenas registra warning — a
    /// regra é suprimir só com evidência positiva.
    /// </summary>
    private async Task<(string Source, string Reason)?> CheckMaintenanceWindowAsync(
        AgentMonitoringEvent monitoringEvent, CancellationToken ct)
    {
        var json = await db.ClientConfigurations.AsNoTracking()
            .Where(c => c.ClientId == monitoringEvent.ClientId)
            .Select(c => c.MaintenanceWindowJson)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(json) && monitoringEvent.SiteId.HasValue)
        {
            json = await db.SiteConfigurations.AsNoTracking()
                .Where(s => s.SiteId == monitoringEvent.SiteId.Value)
                .Select(s => s.MaintenanceWindowJson)
                .FirstOrDefaultAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("enabled", out var enabledProp) || !enabledProp.GetBoolean())
                return null;

            var now = DateTime.UtcNow;

            if (root.TryGetProperty("daysOfWeek", out var daysProp) && daysProp.ValueKind == JsonValueKind.Array)
            {
                var today = (int)now.DayOfWeek;
                var matchesDay = false;
                foreach (var d in daysProp.EnumerateArray())
                {
                    if (d.TryGetInt32(out var day) && day == today) { matchesDay = true; break; }
                }
                if (!matchesDay) return null;
            }

            if (!root.TryGetProperty("startUtc", out var startProp)) return null;
            if (!TimeOnly.TryParse(startProp.GetString(), out var start)) return null;

            var durationMinutes = root.TryGetProperty("durationMinutes", out var durProp) && durProp.TryGetInt32(out var dm)
                ? dm
                : 60;

            var startToday = now.Date.Add(start.ToTimeSpan());
            var endToday = startToday.AddMinutes(durationMinutes);

            if (now >= startToday && now < endToday)
            {
                return ("Window", $"Active maintenance window until {endToday:u} (UTC).");
            }

            return null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "MaintenanceWindowJson inválido para o client {ClientId}; auto-ticket NÃO será suprimido (fail-open).",
                monitoringEvent.ClientId);
            return null;
        }
    }

    private async Task<(int Transitions, bool IsFlapping)> DetectFlappingAsync(
        AgentMonitoringEvent monitoringEvent,
        ResolvedAutoTicketPolicy policy,
        string? sensorKey,
        CancellationToken ct)
    {
        if (policy.FlapWindowMinutes <= 0 || policy.FlapTransitionsThreshold <= 0)
            return (0, false);

        var since = DateTime.UtcNow.AddMinutes(-policy.FlapWindowMinutes);

        // Conta eventos do mesmo alert code/agente na janela: muitas transições
        // no mesmo alvo indicam flapping.
        var transitions = await db.AgentMonitoringEvents.AsNoTracking()
            .Where(e => e.AgentId == monitoringEvent.AgentId
                        && e.AlertCode == monitoringEvent.AlertCode
                        && e.OccurredAt >= since)
            .CountAsync(ct);

        return (transitions, transitions > policy.FlapTransitionsThreshold);
    }

    private async Task<(int ConsecutiveFailures, int DurationSeconds, bool IsPersistent, AutoTicketImpact Impact)>
        EvaluatePersistenceAsync(
            AgentMonitoringEvent monitoringEvent,
            ResolvedAutoTicketPolicy policy,
            int minDurationSeconds,
            CancellationToken ct)
    {
        var state = await db.AgentSensorStates.AsNoTracking()
            .Where(s => s.AgentId == monitoringEvent.AgentId
                        && s.SensorId == (monitoringEvent.SourceRefId ?? Guid.Empty))
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new { s.ConsecutiveFailures, s.LastStatusChangedAt, s.PrimaryValue, s.Status })
            .FirstOrDefaultAsync(ct);

        var consecutiveFailures = state?.ConsecutiveFailures ?? 1;

        var since = state?.LastStatusChangedAt ?? monitoringEvent.OccurredAt;
        var durationSeconds = (int)Math.Max(0, (DateTime.UtcNow - since).TotalSeconds);

        var failuresOk = consecutiveFailures >= policy.MinConsecutiveFailures;
        var durationOk = durationSeconds >= minDurationSeconds;

        var impact = MapSeverityToImpact(monitoringEvent.Severity);
        var isPersistent = failuresOk && durationOk;

        return (consecutiveFailures, durationSeconds, isPersistent, impact);
    }

    private static SensorSeverity MapSeverity(MonitoringEventSeverity severity) => severity switch
    {
        MonitoringEventSeverity.Critical => SensorSeverity.Critical,
        MonitoringEventSeverity.Warning => SensorSeverity.Warning,
        _ => SensorSeverity.Info
    };

    private static AutoTicketImpact MapSeverityToImpact(MonitoringEventSeverity severity) => severity switch
    {
        MonitoringEventSeverity.Critical => AutoTicketImpact.High,
        MonitoringEventSeverity.Warning => AutoTicketImpact.Medium,
        MonitoringEventSeverity.Attention => AutoTicketImpact.Low,
        _ => AutoTicketImpact.Low
    };

    private static AutoTicketImpact MaxImpact(
        SensorSeverity severity, AutoTicketImpact current, ResolvedAutoTicketPolicy policy)
    {
        var fromSeverity = severity switch
        {
            SensorSeverity.Critical => AutoTicketImpact.High,
            SensorSeverity.Warning => AutoTicketImpact.Medium,
            _ => AutoTicketImpact.Low
        };
        return fromSeverity > current ? fromSeverity : current;
    }
}
