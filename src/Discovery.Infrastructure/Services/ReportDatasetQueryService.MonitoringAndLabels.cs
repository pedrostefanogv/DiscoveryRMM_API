using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public partial class ReportDatasetQueryService
{
    private async Task<ReportQueryResult> QueryAgentLabelsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var labelName = GetString(filters, "labelName");
        var orderBy = GetEnum(filters, "orderBy", AgentLabelsOrderBy.LabelName);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query =
            from al in _db.AgentLabels.AsNoTracking()
            join ag in _db.Agents.AsNoTracking() on al.AgentId equals ag.Id
            join st in _db.Sites.AsNoTracking() on ag.SiteId equals st.Id
            join cli in _db.Clients.AsNoTracking() on st.ClientId equals cli.Id
            where ag.DeletedAt == null && (!clientId.HasValue || st.ClientId == clientId.Value)
            select new
            {
                AgentId = ag.Id,
                AgentHostname = ag.Hostname,
                AgentDisplayName = ag.DisplayName,
                ClientId = cli.Id,
                ClientName = cli.Name,
                SiteId = st.Id,
                SiteName = st.Name,
                al.Label,
                al.SourceType,
                al.CreatedAt
            };

        if (siteId.HasValue)
            query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue)
            query = query.Where(x => x.AgentId == agentId.Value);
        if (!string.IsNullOrWhiteSpace(labelName))
            query = query.Where(x => EF.Functions.ILike(x.Label, $"%{labelName}%"));

        var orderedQuery = orderBy switch
        {
            AgentLabelsOrderBy.AgentHostname => descending
                ? query.OrderByDescending(x => x.AgentHostname).ThenBy(x => x.Label)
                : query.OrderBy(x => x.AgentHostname).ThenBy(x => x.Label),
            AgentLabelsOrderBy.SiteName => descending
                ? query.OrderByDescending(x => x.SiteName).ThenBy(x => x.Label)
                : query.OrderBy(x => x.SiteName).ThenBy(x => x.Label),
            AgentLabelsOrderBy.AppliedAt => descending
                ? query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Label)
                : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.Label),
            _ => descending
                ? query.OrderByDescending(x => x.Label).ThenBy(x => x.AgentHostname)
                : query.OrderBy(x => x.Label).ThenBy(x => x.AgentHostname)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["agentId"] = x.AgentId,
                ["agentHostname"] = x.AgentHostname,
                ["agentName"] = x.AgentDisplayName,
                ["clientId"] = x.ClientId,
                ["clientName"] = x.ClientName,
                ["siteId"] = x.SiteId,
                ["siteName"] = x.SiteName,
                ["labelName"] = x.Label,
                ["labelSource"] = x.SourceType.ToString(),
                ["labelAppliedAt"] = x.CreatedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["agentId", "agentHostname", "agentName", "clientId", "clientName", "siteId", "siteName", "labelName", "labelSource", "labelAppliedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryAutomaticLabelRulesAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var labelName = GetString(filters, "labelName");
        var orderBy = GetEnum(filters, "orderBy", AutomaticLabelRulesOrderBy.RuleName);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query =
            from lr in _db.AgentLabelRules.AsNoTracking()
            join lrm in _db.AgentLabelRuleMatches.AsNoTracking() on lr.Id equals lrm.RuleId into matchesJoin
            from lrm in matchesJoin.DefaultIfEmpty()
            join ag in _db.Agents.AsNoTracking() on lrm != null ? lrm.AgentId : Guid.Empty equals ag.Id into agentsJoin
            from ag in agentsJoin.DefaultIfEmpty()
            // As regras nao tem client_id: o escopo vem do agente afetado -> site ->
            // cliente. Sem esse join o dataset vazava regras e hostnames de TODOS
            // os clientes, mesmo em execucao com ClientId definido.
            join st in _db.Sites.AsNoTracking() on (ag != null ? ag.SiteId : Guid.Empty) equals st.Id into sitesJoin
            from st in sitesJoin.DefaultIfEmpty()
            where (!clientId.HasValue || (st != null && st.ClientId == clientId.Value))
                  && (!siteId.HasValue || (st != null && st.Id == siteId.Value))
            group new { lr, ag } by new { lr.Id, lr.Name, lr.Label, lr.Description, lr.IsEnabled, lr.ExpressionJson, lr.CreatedAt } into g
            select new
            {
                g.Key.Id,
                RuleName = g.Key.Name,
                LabelName = g.Key.Label,
                RuleDescription = g.Key.Description,
                IsActive = g.Key.IsEnabled,
                ConditionExpression = g.Key.ExpressionJson,
                MatchCount = g.Count(x => x.ag != null),
                AffectedAgentHostnames = g.Where(x => x.ag != null).Select(x => x.ag.Hostname).Distinct().ToList(),
                g.Key.CreatedAt
            };

        if (!string.IsNullOrWhiteSpace(labelName))
            query = query.Where(x => EF.Functions.ILike(x.LabelName, $"%{labelName}%"));

        var orderedQuery = orderBy switch
        {
            AutomaticLabelRulesOrderBy.LabelName => descending
                ? query.OrderByDescending(x => x.LabelName).ThenBy(x => x.RuleName)
                : query.OrderBy(x => x.LabelName).ThenBy(x => x.RuleName),
            AutomaticLabelRulesOrderBy.MatchCount => descending
                ? query.OrderByDescending(x => x.MatchCount).ThenBy(x => x.RuleName)
                : query.OrderBy(x => x.MatchCount).ThenBy(x => x.RuleName),
            AutomaticLabelRulesOrderBy.CreatedAt => descending
                ? query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.RuleName)
                : query.OrderBy(x => x.CreatedAt).ThenBy(x => x.RuleName),
            _ => descending
                ? query.OrderByDescending(x => x.RuleName)
                : query.OrderBy(x => x.RuleName)
        };

        var rowsRaw = await orderedQuery
            .Take(limit)
            .ToListAsync(cancellationToken);

        var rows = rowsRaw
            .Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["ruleId"] = x.Id,
                ["ruleName"] = x.RuleName,
                ["labelName"] = x.LabelName,
                ["ruleDescription"] = x.RuleDescription,
                ["isActive"] = x.IsActive,
                ["conditionExpression"] = x.ConditionExpression,
                ["matchCount"] = x.MatchCount,
                ["affectedAgentHostnames"] = string.Join(", ", x.AffectedAgentHostnames ?? []),
                ["createdAt"] = x.CreatedAt
            })
            .ToList();

        return new ReportQueryResult
        {
            Columns = ["ruleId", "ruleName", "labelName", "ruleDescription", "isActive", "conditionExpression", "matchCount", "affectedAgentHostnames", "createdAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryAgentMonitoringEventsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var severity = GetString(filters, "severity");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.AgentMonitoringEvents.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);
        if (siteId.HasValue) query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue) query = query.Where(x => x.AgentId == agentId.Value);
        if (!string.IsNullOrWhiteSpace(severity) && Enum.TryParse<MonitoringEventSeverity>(severity.Trim(), ignoreCase: true, out var parsedSeverity))
            query = query.Where(x => x.Severity == parsedSeverity);
        if (from.HasValue) query = query.Where(x => x.OccurredAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.OccurredAt <= to.Value);

        query = descending
            ? query.OrderByDescending(x => x.OccurredAt)
            : query.OrderBy(x => x.OccurredAt);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["clientId"] = x.ClientId, ["siteId"] = x.SiteId, ["agentId"] = x.AgentId,
            ["alertCode"] = x.AlertCode, ["severity"] = x.Severity.ToString(), ["title"] = x.Title,
            ["message"] = x.Message, ["metricKey"] = x.MetricKey, ["metricValue"] = x.MetricValue,
            ["source"] = x.Source.ToString(), ["correlationId"] = x.CorrelationId,
            ["occurredAt"] = x.OccurredAt, ["createdAt"] = x.CreatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "clientId", "siteId", "agentId", "alertCode", "severity", "title", "message", "metricKey", "metricValue", "source", "correlationId", "occurredAt", "createdAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryAgentAlertsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var descending = GetSortDescending(filters, defaultValue: false);

        var query = _db.AgentAlertDefinitions.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);

        query = descending
            ? query.OrderByDescending(x => x.Name)
            : query.OrderBy(x => x.Name);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["id"] = x.Id, ["clientId"] = x.ClientId, ["name"] = x.Name,
            ["severity"] = x.Severity.ToString(), ["enabled"] = x.IsEnabled,
            ["metricKey"] = x.MetricKey, ["threshold"] = x.Threshold,
            ["scopeType"] = x.ScopeType.ToString(), ["createdAt"] = x.CreatedAt, ["updatedAt"] = x.UpdatedAt
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "clientId", "name", "severity", "enabled", "metricKey", "threshold", "scopeType", "createdAt", "updatedAt"],
            Rows = rows
        };
    }

    /// <summary>
    /// Estado corrente dos sensores (uma linha por agente/sensor/alvo). Serve
    /// para relatórios de conformidade/frota: "quais dispositivos estão com
    /// disco crítico", "quais sensores estão sem coletar" etc.
    /// </summary>
    private async Task<ReportQueryResult> QueryAgentSensorsAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var statusFilter = GetString(filters, "status");
        var severityFilter = GetString(filters, "severity");
        var sensorTypeFilter = GetString(filters, "sensorType");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");

        var query =
            from state in _db.AgentSensorStates.AsNoTracking()
            join agent in _db.Agents.AsNoTracking() on state.AgentId equals agent.Id
            join site in _db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in _db.Clients.AsNoTracking() on site.ClientId equals client.Id
            join definition in _db.SensorDefinitions.AsNoTracking() on state.SensorId equals definition.Id
            where agent.DeletedAt == null
            select new { state, agent, site, client, definition };

        if (clientId.HasValue) query = query.Where(x => x.client.Id == clientId.Value);
        if (siteId.HasValue) query = query.Where(x => x.site.Id == siteId.Value);
        if (agentId.HasValue) query = query.Where(x => x.agent.Id == agentId.Value);

        if (!string.IsNullOrWhiteSpace(statusFilter)
            && Enum.TryParse<Discovery.Core.Enums.SensorStatus>(statusFilter, ignoreCase: true, out var parsedStatus))
        {
            query = query.Where(x => x.state.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(severityFilter)
            && Enum.TryParse<Discovery.Core.Enums.SensorSeverity>(severityFilter, ignoreCase: true, out var parsedSeverity))
        {
            query = query.Where(x => x.state.Severity == parsedSeverity);
        }

        if (!string.IsNullOrWhiteSpace(sensorTypeFilter)
            && Enum.TryParse<Discovery.Core.Enums.SensorType>(sensorTypeFilter, ignoreCase: true, out var parsedType))
        {
            query = query.Where(x => x.definition.SensorType == parsedType);
        }

        if (from.HasValue) query = query.Where(x => x.state.LastRunAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.state.LastRunAt <= to.Value);

        // Pior primeiro: falha > atenção > sem dados > ok. É a ordem que um
        // relatório de exceção precisa.
        query = query
            .OrderByDescending(x => x.state.Status == Discovery.Core.Enums.SensorStatus.Failing)
            .ThenByDescending(x => x.state.Status == Discovery.Core.Enums.SensorStatus.Warning)
            .ThenByDescending(x => x.state.LastRunAt);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        var rows = rowsRaw.Select(x =>
        {
            var channels = SensorChannelCatalog.Resolve(x.definition);
            var unit = channels.FirstOrDefault(channel =>
                string.Equals(channel.Key, x.state.PrimaryChannel, StringComparison.OrdinalIgnoreCase))?.Unit.ToString()
                ?? x.definition.ThresholdUnit.ToString();

            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["clientId"] = x.client.Id,
                ["clientName"] = x.client.Name,
                ["siteId"] = x.site.Id,
                ["siteName"] = x.site.Name,
                ["agentId"] = x.agent.Id,
                ["agentHostname"] = x.agent.DisplayName ?? x.agent.Hostname,
                ["sensorId"] = x.state.SensorId,
                ["sensorName"] = x.definition.Name,
                ["sensorKey"] = x.definition.Key,
                ["sensorType"] = x.definition.SensorType.ToString(),
                ["target"] = x.definition.Target,
                ["targetKey"] = x.state.TargetKey,
                ["status"] = x.state.Status.ToString(),
                ["severity"] = x.state.Severity?.ToString(),
                ["primaryChannel"] = x.state.PrimaryChannel,
                ["primaryValue"] = x.state.PrimaryValue,
                ["unit"] = unit,
                ["message"] = x.state.Message,
                ["consecutiveFailures"] = x.state.ConsecutiveFailures,
                ["totalRuns"] = x.state.TotalRuns,
                ["totalFailures"] = x.state.TotalFailures,
                ["warningThreshold"] = x.definition.WarningThreshold,
                ["criticalThreshold"] = x.definition.CriticalThreshold,
                ["comparison"] = x.definition.Comparison.ToString(),
                ["lastRunAt"] = x.state.LastRunAt,
                ["lastStatusChangedAt"] = x.state.LastStatusChangedAt
            };
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["clientId", "clientName", "siteId", "siteName", "agentId", "agentHostname",
                       "sensorId", "sensorName", "sensorKey", "sensorType", "target", "targetKey",
                       "status", "severity", "primaryChannel", "primaryValue", "unit",
                       "message", "consecutiveFailures", "totalRuns", "totalFailures",
                       "warningThreshold", "criticalThreshold", "comparison", "lastRunAt", "lastStatusChangedAt"],
            Rows = rows
        };
    }

    private async Task<ReportQueryResult> QueryP2pTelemetryAsync(Guid? clientId, JsonElement filters, CancellationToken cancellationToken)
    {
        var limit = GetLimit(filters);
        var siteId = GetGuid(filters, "siteId");
        var agentId = GetGuid(filters, "agentId");
        var from = GetDateTime(filters, "from");
        var to = GetDateTime(filters, "to");
        var descending = GetSortDescending(filters, defaultValue: true);

        var query = _db.P2pAgentTelemetries.AsNoTracking().AsQueryable();
        if (clientId.HasValue) query = query.Where(x => x.ClientId == clientId.Value);
        if (siteId.HasValue) query = query.Where(x => x.SiteId == siteId.Value);
        if (agentId.HasValue) query = query.Where(x => x.AgentId == agentId.Value);
        if (from.HasValue) query = query.Where(x => x.CollectedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.CollectedAt <= to.Value);

        query = descending
            ? query.OrderByDescending(x => x.CollectedAt)
            : query.OrderBy(x => x.CollectedAt);

        var rowsRaw = await query.Take(limit).ToListAsync(cancellationToken);

        // Os contadores do agent são CUMULATIVOS e zeram quando ele reinicia. Expor
        // o valor cru fazia qualquer agregação do relatório (SUM/AVG) somar o
        // acumulado em vez do tráfego do período. As colunas de métrica passam a
        // trazer o incremento (regra reset-aware) e o valor cru fica explícito nas
        // colunas *Cumulative.
        var deltas = ComputeP2pTelemetryDeltas(rowsRaw);

        var rows = rowsRaw.Select(x =>
        {
            var delta = deltas[x.Id];
            return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = x.Id, ["agentId"] = x.AgentId, ["siteId"] = x.SiteId, ["clientId"] = x.ClientId,
                ["collectedAt"] = x.CollectedAt, ["receivedAt"] = x.ReceivedAt,
                ["publishedArtifacts"] = x.PublishedArtifacts,
                ["replicationsStarted"] = delta.Started,
                ["replicationsSucceeded"] = delta.Succeeded,
                ["replicationsFailed"] = delta.Failed,
                ["bytesServed"] = delta.Served,
                ["bytesDownloaded"] = delta.Downloaded,
                ["bytesServedCumulative"] = x.BytesServed,
                ["bytesDownloadedCumulative"] = x.BytesDownloaded,
                ["replicationsStartedCumulative"] = x.ReplicationsStarted,
                ["replicationsSucceededCumulative"] = x.ReplicationsSucceeded,
                ["replicationsFailedCumulative"] = x.ReplicationsFailed,
                ["activeReplications"] = x.ActiveReplications,
                ["hostCpuPercent"] = x.HostCpuPercent, ["hostMemoryPercent"] = x.HostMemoryPercent,
                ["hostDiskBusyPercent"] = x.HostDiskBusyPercent, ["hostCpuCores"] = x.HostCpuCores,
                ["hostRamGB"] = x.HostRamGB, ["knownPeers"] = x.KnownPeers, ["connectedPeers"] = x.ConnectedPeers,
                ["planTotalAgents"] = x.PlanTotalAgents, ["planSelectedSeeds"] = x.PlanSelectedSeeds
            };
        }).ToList();

        return new ReportQueryResult
        {
            Columns = ["id", "agentId", "siteId", "clientId", "collectedAt", "receivedAt", "publishedArtifacts",
                "replicationsStarted", "replicationsSucceeded", "replicationsFailed", "bytesServed", "bytesDownloaded",
                "replicationsStartedCumulative", "replicationsSucceededCumulative", "replicationsFailedCumulative",
                "bytesServedCumulative", "bytesDownloadedCumulative", "activeReplications",
                "hostCpuPercent", "hostMemoryPercent", "hostDiskBusyPercent", "hostCpuCores", "hostRamGB",
                "knownPeers", "connectedPeers", "planTotalAgents", "planSelectedSeeds"],
            Rows = rows
        };
    }

    /// <summary>
    /// Incrementos reset-aware dos contadores cumulativos por agente:
    ///   - sem amostra anterior -> 0 (baseline desconhecido; não inflar o relatório);
    ///   - valor &gt;= anterior -> diferença;
    ///   - valor &lt; anterior (restart do agent) -> valor atual (novo segmento).
    /// Ordena por collected_at (relógio do snapshot) para tolerar replay fora de ordem.
    /// </summary>
    internal static Dictionary<long, (long Served, long Downloaded, long Started, long Succeeded, long Failed)>
        ComputeP2pTelemetryDeltas(IReadOnlyList<P2pAgentTelemetry> rows)
    {
        var result = new Dictionary<long, (long, long, long, long, long)>();
        foreach (var group in rows.GroupBy(row => row.AgentId))
        {
            P2pAgentTelemetry? previous = null;
            foreach (var current in group.OrderBy(row => row.CollectedAt).ThenBy(row => row.Id))
            {
                result[current.Id] = (
                    CounterDelta(previous?.BytesServed, current.BytesServed),
                    CounterDelta(previous?.BytesDownloaded, current.BytesDownloaded),
                    CounterDelta(previous?.ReplicationsStarted, current.ReplicationsStarted),
                    CounterDelta(previous?.ReplicationsSucceeded, current.ReplicationsSucceeded),
                    CounterDelta(previous?.ReplicationsFailed, current.ReplicationsFailed));
                previous = current;
            }
        }
        return result;
    }

    /// <summary>Incremento de um contador cumulativo (ver ComputeP2pTelemetryDeltas).</summary>
    internal static long CounterDelta(long? previous, long current)
    {
        if (!previous.HasValue)
            return 0;
        return current >= previous.Value ? current - previous.Value : current;
    }
}
