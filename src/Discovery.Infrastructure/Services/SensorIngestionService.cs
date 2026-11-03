using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Core.Configuration;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Ingestão de amostras de sensores: revalida a evidência enviada pelo agente,
/// calcula status/severidade (fonte única), atualiza estado/sparkline, grava a
/// série e decide alertas (AgentMonitoringEvent), auto-ticket, notificações e
/// evento de dashboard na transição.
/// </summary>
public sealed class SensorIngestionService(
    DiscoveryDbContext db,
    ISensorDefinitionService definitionService,
    IAutoTicketOrchestratorService autoTicket,
    INotificationChannelDispatcher notificationChannels,
    INotificationService notifications,
    IAgentMessaging messaging,
    ILogger<SensorIngestionService> logger) : ISensorIngestionService
{
    /// <summary>Alerta emitido nesta ingestão (para auto-ticket/notificação/dashboard).</summary>
    private sealed record AlertEmission(
        AgentMonitoringEvent Event,
        string Transition,
        SensorSeverity Severity,
        string SensorName,
        Guid AgentId,
        Guid SensorId,
        Guid? ClientId,
        Guid? SiteId,
        SensorStatus Status,
        string? TargetKey);

    public async Task<AgentSensorResultResponse> IngestAsync(AgentSensorResultBatch batch, CancellationToken cancellationToken = default)
    {
        var response = new AgentSensorResultResponse();
        if (batch.Samples.Count == 0) return response;

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == batch.AgentId, cancellationToken);
        if (agent is null)
        {
            response.Rejected = batch.Samples
                .Select(s => new AgentSensorRejectedDto { SensorId = s.SensorId, TargetKey = s.TargetKey, Reason = "agent not found" })
                .ToList();
            return response;
        }

        var settings = await SensorSettingsStore.LoadAsync(db, cancellationToken);
        var applicable = await definitionService.ResolveApplicableAsync(agent.Id, cancellationToken);
        var definitionsById = applicable.ToDictionary(d => d.Id);

        var clientId = await db.Sites.AsNoTracking()
            .Where(s => s.Id == agent.SiteId)
            .Select(s => (Guid?)s.ClientId)
            .FirstOrDefaultAsync(cancellationToken);

        var sensorIds = definitionsById.Keys.ToList();
        var existingStates = sensorIds.Count == 0
            ? new List<AgentSensorState>()
            : await db.AgentSensorStates
                .Where(s => s.AgentId == agent.Id && sensorIds.Contains(s.SensorId))
                .ToListAsync(cancellationToken);

        var stateByKey = existingStates.ToDictionary(s => (s.SensorId, s.TargetKey), s => s);
        var now = DateTime.UtcNow;
        var rejected = new List<AgentSensorRejectedDto>();
        var emissions = new List<AlertEmission>();

        foreach (var sample in batch.Samples.Take(SensorDefaults.MaxBatchSamples))
        {
            if (!definitionsById.TryGetValue(sample.SensorId, out var definition))
            {
                rejected.Add(new AgentSensorRejectedDto
                {
                    SensorId = sample.SensorId,
                    TargetKey = sample.TargetKey,
                    Reason = "sensor not applicable"
                });
                continue;
            }

            var runId = sample.RunId == Guid.Empty ? Guid.NewGuid() : sample.RunId;
            var targetKey = Truncate(sample.TargetKey, 100) ?? string.Empty;

            stateByKey.TryGetValue((definition.Id, targetKey), out var state);
            if (state is not null && state.LastRunId == runId)
            {
                // Retry do mesmo run: idempotente.
                response.Accepted++;
                continue;
            }

            var evaluation = SensorEvaluator.Evaluate(
                definition,
                sample.Measure,
                sample.Error,
                sample.DeclaredStatus,
                sample.DeclaredMessage,
                sample.Channels,
                targetKey);

            var occurredAt = NormalizeTimestamp(sample.FinishedAt) ?? now;
            var capturedAt = now;

            if (state is null)
            {
                state = new AgentSensorState
                {
                    Id = Guid.NewGuid(),
                    AgentId = agent.Id,
                    SensorId = definition.Id,
                    TargetKey = targetKey,
                    CreatedAt = now
                };
                db.AgentSensorStates.Add(state);
                stateByKey[(definition.Id, targetKey)] = state;
            }

            var previousStatus = state.Status;
            var previousSeverity = state.Severity;
            var hasPreviousRun = state.LastRunId is not null || state.TotalRuns > 0;

            // Histerese: segura a ESCALAÇÃO (Warning → Failing) até acumular
            // SustainSamples amostras ruins. A primeira falha e a recuperação
            // continuam imediatas.
            var effectiveStatus = evaluation.Status;
            var effectiveSeverity = evaluation.Severity;
            var heldEscalation = hasPreviousRun
                && SensorEvaluator.ShouldHoldEscalation(
                    definition, previousStatus, evaluation.Status, state.ConsecutiveFailures);

            if (heldEscalation)
            {
                effectiveStatus = previousStatus;
                effectiveSeverity = previousSeverity;
            }

            state.Status = effectiveStatus;
            state.Severity = effectiveSeverity;
            state.Message = Truncate(
                heldEscalation
                    ? $"{evaluation.Message} (escalação pendente: {state.ConsecutiveFailures + 1}/{definition.SustainSamples} amostras)"
                    : evaluation.Message,
                1000);
            state.ChannelsJson = SensorJson.WriteChannelsValues(evaluation.Channels);
            state.PrimaryChannel = evaluation.PrimaryChannel;
            state.PrimaryValue = evaluation.PrimaryValue;
            state.ExitCode = sample.Measure.ExitCode;
            state.DurationMs = sample.DurationMs;
            state.OutputExcerpt = BuildOutputExcerpt(sample);
            state.LastRunAt = occurredAt;
            state.LastRunId = runId;
            state.TotalRuns++;
            state.UpdatedAt = now;

            // Contadores usam o status BRUTO (o que foi medido), não o efetivo:
            // com histerese segurando a escalação (ex.: Unknown → Failing), o
            // contador precisa avançar para a escalação acontecer na N-ésima
            // amostra — senão ficaria preso para sempre.
            var isFailure = evaluation.Status is SensorStatus.Warning or SensorStatus.Failing;
            if (isFailure)
            {
                state.ConsecutiveFailures++;
                state.TotalFailures++;
            }
            else if (evaluation.Status == SensorStatus.Passing)
            {
                state.ConsecutiveFailures = 0;
            }
            else if (evaluation.Status == SensorStatus.Unknown)
            {
                state.TotalFailures++;
            }

            state.SparklineJson = AppendSparkline(state.SparklineJson, evaluation);

            var statusChanged = previousStatus != effectiveStatus;
            var severityChanged = previousSeverity != effectiveSeverity;
            if (!hasPreviousRun)
            {
                AddEvent(agent.Id, definition.Id, targetKey, runId, SensorEventKind.FirstRun,
                    effectiveStatus, effectiveSeverity, null, null,
                    evaluation.Message, evaluation.Channels, sample.Measure.ExitCode, occurredAt);
            }
            else if (statusChanged || severityChanged)
            {
                state.LastStatusChangedAt = occurredAt;
                AddEvent(agent.Id, definition.Id, targetKey, runId,
                    statusChanged ? SensorEventKind.StatusChanged : SensorEventKind.SeverityChanged,
                    effectiveStatus, effectiveSeverity, previousStatus, previousSeverity,
                    evaluation.Message, evaluation.Channels, sample.Measure.ExitCode, occurredAt);
            }

            if (evaluation.Status == SensorStatus.Unknown && definition.AlertOnSensorError)
            {
                AddEvent(agent.Id, definition.Id, targetKey, runId, SensorEventKind.RunError,
                    evaluation.Status, evaluation.Severity, previousStatus, previousSeverity,
                    evaluation.Message, evaluation.Channels, sample.Measure.ExitCode, occurredAt);
            }

            var effectiveEvaluation = evaluation with { Status = effectiveStatus, Severity = effectiveSeverity };

            var raised = MaybeRaiseAlert(agent, definition, state, effectiveEvaluation, clientId, runId, batch.CorrelationId, occurredAt);
            if (raised is not null)
            {
                emissions.Add(new AlertEmission(raised.Value.Event, raised.Value.Transition,
                    effectiveSeverity ?? definition.SeverityWhenFailing, definition.Name,
                    agent.Id, definition.Id, clientId, agent.SiteId, effectiveStatus, targetKey));
            }

            var resolved = MaybeResolveAlert(agent, definition, state, effectiveEvaluation, clientId, runId, batch.CorrelationId, occurredAt, previousStatus);
            if (resolved is not null)
            {
                emissions.Add(new AlertEmission(resolved, "resolved", SensorSeverity.Info, definition.Name,
                    agent.Id, definition.Id, clientId, agent.SiteId, effectiveStatus, targetKey));
            }

            if (definition.StoreSamples)
            {
                db.AgentSensorSamples.Add(new AgentSensorSample
                {
                    AgentId = agent.Id,
                    SensorId = definition.Id,
                    TargetKey = targetKey,
                    CollectedAt = occurredAt,
                    CapturedAt = capturedAt,
                    Status = effectiveStatus,
                    Severity = effectiveSeverity,
                    ChannelsJson = SensorJson.WriteChannelsValues(evaluation.Channels),
                    Message = Truncate(evaluation.Message, 500),
                    RunId = runId,
                    ExitCode = sample.Measure.ExitCode
                });
            }

            response.Accepted++;
        }

        if (batch.Samples.Count > SensorDefaults.MaxBatchSamples)
        {
            rejected.AddRange(batch.Samples.Skip(SensorDefaults.MaxBatchSamples).Select(s => new AgentSensorRejectedDto
            {
                SensorId = s.SensorId,
                TargetKey = s.TargetKey,
                Reason = $"batch limit of {SensorDefaults.MaxBatchSamples} samples exceeded"
            }));
        }

        await db.SaveChangesAsync(cancellationToken);
        response.Rejected = rejected;

        // Pós-persistência: auto-ticket, notificações e evento de dashboard. Cada
        // integração é best-effort — uma falha aqui NÃO desfaz a amostra.
        if (emissions.Count > 0)
        {
            await RunPostIngestionSideEffectsAsync(emissions, settings, cancellationToken);
        }

        if (rejected.Count > 0)
        {
            logger.LogDebug("Sensor ingestion rejected {Rejected} sample(s) for agent {AgentId}.", rejected.Count, agent.Id);
        }

        return response;
    }

    private async Task RunPostIngestionSideEffectsAsync(
        IReadOnlyList<AlertEmission> emissions,
        SensorsSettings settings,
        CancellationToken cancellationToken)
    {
        foreach (var emission in emissions)
        {
            // 1) Auto-ticket só na abertura/escala (resolução fecha por si via ticket
            //    quando a regra existir; não deve criar ticket novo).
            if (settings.AutoTicketEnabled && emission.Transition != "resolved")
            {
                try
                {
                    await autoTicket.EvaluateAsync(emission.Event, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Auto-ticket falhou para o evento de sensor {SensorId} do agente {AgentId}.", emission.SensorId, emission.AgentId);
                }
            }

            // 2) Notificações internas + canais externos (e-mail/webhook).
            if (settings.NotifyEnabled)
            {
                var request = new NotificationPublishRequest(
                    EventType: "SensorAlert",
                    Topic: "sensors",
                    Title: emission.Transition == "resolved"
                        ? $"Sensor normalizado: {emission.SensorName}"
                        : $"Sensor em alerta: {emission.SensorName}",
                    Message: emission.Event.Message,
                    Severity: emission.Transition == "resolved"
                        ? NotificationSeverity.Informational
                        : MapNotificationSeverity(emission.Severity),
                    Payload: new
                    {
                        sensorId = emission.SensorId,
                        agentId = emission.AgentId,
                        transition = emission.Transition,
                        status = emission.Status.ToString(),
                        severity = emission.Severity.ToString(),
                        targetKey = emission.TargetKey
                    },
                    RecipientAgentId: emission.AgentId);

                try
                {
                    await notifications.PublishAsync(request, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Notificação in-app falhou para o sensor {SensorId}.", emission.SensorId);
                }

                try
                {
                    await notificationChannels.DispatchAsync(request, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Despacho para canais externos falhou para o sensor {SensorId}.", emission.SensorId);
                }
            }

            // 3) Evento de dashboard (tempo real no console). Independe das flags
            //    de notificação: é atualização de UI, não alerta.
            try
            {
                var dashboardEvent = DashboardEventMessage.Create(
                    "SensorStatusChanged",
                    new
                    {
                        agentId = emission.AgentId,
                        sensorId = emission.SensorId,
                        sensorName = emission.SensorName,
                        status = emission.Status.ToString(),
                        severity = emission.Severity.ToString(),
                        transition = emission.Transition,
                        targetKey = emission.TargetKey
                    },
                    emission.ClientId,
                    emission.SiteId);

                await messaging.PublishDashboardEventAsync(dashboardEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Publicação do evento de dashboard falhou para o sensor {SensorId}.", emission.SensorId);
            }
        }
    }

    private static NotificationSeverity MapNotificationSeverity(SensorSeverity severity) => severity switch
    {
        SensorSeverity.Info => NotificationSeverity.Informational,
        SensorSeverity.Warning => NotificationSeverity.Warning,
        _ => NotificationSeverity.Critical
    };

    private (AgentMonitoringEvent Event, string Transition)? MaybeRaiseAlert(
        Agent agent,
        SensorDefinition definition,
        AgentSensorState state,
        SensorEvaluation evaluation,
        Guid? clientId,
        Guid runId,
        string? correlationId,
        DateTime occurredAt)
    {
        var isUnknown = evaluation.Status == SensorStatus.Unknown;
        var isFailure = evaluation.Status is SensorStatus.Warning or SensorStatus.Failing || (isUnknown && definition.AlertOnSensorError);
        if (!isFailure) return null;

        var severity = evaluation.Severity ?? (isUnknown ? SensorSeverity.Warning : null);
        if (severity is null) return null;
        if (severity.Value < definition.MinSeverityToAlert) return null;
        if (state.ConsecutiveFailures < definition.FailsBeforeAlert && !isUnknown) return null;
        if (agent.MaintenanceEnabled) return null;

        var alreadyAlerted = state.LastAlertedAt is not null;
        var escalated = state.LastAlertSeverity is not null && severity.Value > state.LastAlertSeverity.Value;
        if (alreadyAlerted && !escalated) return null;

        state.LastAlertedAt = occurredAt;
        state.LastAlertSeverity = severity;
        AddEvent(agent.Id, definition.Id, state.TargetKey, runId, SensorEventKind.AlertRaised,
            evaluation.Status, severity, null, null, evaluation.Message, evaluation.Channels, null, occurredAt);

        var monitoringEvent = BuildMonitoringEvent(
            agent, definition, state, evaluation, severity.Value, clientId, correlationId, occurredAt,
            alreadyAlerted ? "escalated" : "opened");

        db.AgentMonitoringEvents.Add(monitoringEvent);
        return (monitoringEvent, alreadyAlerted ? "escalated" : "opened");
    }

    private AgentMonitoringEvent? MaybeResolveAlert(
        Agent agent,
        SensorDefinition definition,
        AgentSensorState state,
        SensorEvaluation evaluation,
        Guid? clientId,
        Guid runId,
        string? correlationId,
        DateTime occurredAt,
        SensorStatus previousStatus)
    {
        if (evaluation.Status != SensorStatus.Passing) return null;
        if (state.LastAlertedAt is null) return null;

        state.LastAlertedAt = null;
        state.LastAlertSeverity = null;
        AddEvent(agent.Id, definition.Id, state.TargetKey, runId, SensorEventKind.AlertResolved,
            evaluation.Status, null, previousStatus, null, evaluation.Message, evaluation.Channels, null, occurredAt);

        var monitoringEvent = BuildMonitoringEvent(
            agent, definition, state, evaluation, SensorSeverity.Info, clientId, correlationId, occurredAt, "resolved");

        db.AgentMonitoringEvents.Add(monitoringEvent);
        return monitoringEvent;
    }

    private static AgentMonitoringEvent BuildMonitoringEvent(
        Agent agent,
        SensorDefinition definition,
        AgentSensorState state,
        SensorEvaluation evaluation,
        SensorSeverity severity,
        Guid? clientId,
        string? correlationId,
        DateTime occurredAt,
        string transition)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId ?? Guid.Empty,
            SiteId = agent.SiteId,
            AgentId = agent.Id,
            AlertCode = "sensor:" + (definition.Key ?? definition.Id.ToString()),
            Severity = MapSeverity(severity),
            Title = definition.Name,
            Message = Truncate(evaluation.Message, 2000) ?? string.Empty,
            MetricKey = evaluation.PrimaryChannel,
            MetricValue = evaluation.PrimaryValue is double value ? (decimal)value : null,
            PayloadJson = JsonSerializer.Serialize(new
            {
                sensorId = definition.Id,
                transition,
                status = evaluation.Status.ToString(),
                severity = evaluation.Severity?.ToString(),
                targetKey = state.TargetKey,
                channels = evaluation.Channels,
                thresholds = new
                {
                    warning = definition.WarningThreshold,
                    critical = definition.CriticalThreshold,
                    comparison = definition.Comparison.ToString()
                },
                exitCode = state.ExitCode
            }),
            Source = MonitoringEventSource.HealthCheck,
            SourceRefId = definition.Id,
            CorrelationId = correlationId,
            OccurredAt = occurredAt,
            CreatedAt = DateTime.UtcNow
        };

    public static MonitoringEventSeverity MapSeverity(SensorSeverity severity) => severity switch
    {
        SensorSeverity.Info => MonitoringEventSeverity.Attention,
        SensorSeverity.Warning => MonitoringEventSeverity.Warning,
        _ => MonitoringEventSeverity.Critical
    };

    private void AddEvent(
        Guid agentId,
        Guid sensorId,
        string targetKey,
        Guid runId,
        SensorEventKind kind,
        SensorStatus status,
        SensorSeverity? severity,
        SensorStatus? previousStatus,
        SensorSeverity? previousSeverity,
        string? message,
        IReadOnlyDictionary<string, double> channels,
        int? exitCode,
        DateTime occurredAt)
    {
        db.AgentSensorEvents.Add(new AgentSensorEvent
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            SensorId = sensorId,
            TargetKey = targetKey,
            RunId = runId,
            Kind = kind,
            Status = status,
            Severity = severity,
            PreviousStatus = previousStatus,
            PreviousSeverity = previousSeverity,
            Message = Truncate(message, 1000),
            ChannelsJson = SensorJson.WriteChannelsValues(channels),
            ExitCode = exitCode,
            OccurredAt = occurredAt,
            CreatedAt = DateTime.UtcNow
        });
    }

    private static string? AppendSparkline(string? currentJson, SensorEvaluation evaluation)
    {
        var sparkline = SensorJson.ParseSparkline(currentJson);

        void Append(string key, double? value)
        {
            if (value is not double actual || !double.IsFinite(actual)) return;
            if (!sparkline.TryGetValue(key, out var list))
            {
                list = [];
                sparkline[key] = list;
            }

            list.Add(actual);
            if (list.Count > SensorDefaults.MaxSparklinePoints)
                list.RemoveRange(0, list.Count - SensorDefaults.MaxSparklinePoints);
        }

        if (evaluation.PrimaryChannel is not null)
            Append(evaluation.PrimaryChannel, evaluation.PrimaryValue);

        foreach (var (key, value) in evaluation.Channels)
        {
            if (key == evaluation.PrimaryChannel) continue;
            Append(key, value);
        }

        return SensorJson.WriteSparkline(sparkline);
    }

    private static string? BuildOutputExcerpt(AgentSensorResultDto sample)
    {
        var combined = string.Join("\n", new[] { sample.Measure.Stdout, sample.Measure.Stderr }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (string.IsNullOrWhiteSpace(combined)) return null;
        combined = combined.Trim();
        return combined.Length <= SensorDefaults.MaxOutputExcerpt
            ? combined
            : combined[^SensorDefaults.MaxOutputExcerpt..];
    }

    private static DateTime? NormalizeTimestamp(DateTime? value)
    {
        if (value is not DateTime actual) return null;
        var utc = actual.Kind == DateTimeKind.Utc ? actual : DateTime.SpecifyKind(actual, DateTimeKind.Utc);
        if (utc > DateTime.UtcNow.AddMinutes(5) || utc < DateTime.UtcNow.AddDays(-7)) return null;
        return utc;
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
}
