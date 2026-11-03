using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Monta o payload da aba "Sensores" do detalhe do agente: definições aplicáveis
/// + estado corrente + contadores + staleness calculada na leitura.
/// </summary>
public sealed class SensorReadService(
    DiscoveryDbContext db,
    ISensorDefinitionService definitionService,
    ISensorSyncService syncService) : ISensorReadService
{
    public async Task<AgentSensorsPayloadDto?> GetForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null) return null;

        var settings = await SensorSettingsStore.LoadAsync(db, cancellationToken);
        var applicable = await definitionService.ResolveApplicableAsync(agentId, cancellationToken);
        var fingerprint = await syncService.GetFingerprintAsync(agentId, cancellationToken);
        var definitionsById = applicable.ToDictionary(d => d.Id);

        var sensorIds = definitionsById.Keys.ToList();
        List<AgentSensorState> states = [];
        if (sensorIds.Count > 0)
        {
            states = await db.AgentSensorStates.AsNoTracking()
                .Where(s => s.AgentId == agentId && sensorIds.Contains(s.SensorId))
                .ToListAsync(cancellationToken);
        }

        var scriptNames = await LoadScriptNamesAsync(applicable, cancellationToken);
        var statesBySensor = states.GroupBy(s => s.SensorId).ToDictionary(g => g.Key, g => g.ToList());

        var now = DateTime.UtcNow;
        var items = new List<SensorItemDto>();

        foreach (var definition in applicable)
        {
            var interval = SensorDefaults.EffectiveInterval(definition);
            var staleAfter = Math.Max(interval * SensorDefaults.StaleMultiplier, 120);

            if (!statesBySensor.TryGetValue(definition.Id, out var sensorStates) || sensorStates.Count == 0)
            {
                var dto = SensorDefinitionService.ToDto(
                    definition, scriptNames,
                    isInherited: definition.ScopeType != AppApprovalScopeType.Agent,
                    sourceScope: definition.ScopeType);
                items.Add(new SensorItemDto
                {
                    SensorId = definition.Id,
                    Key = dto.Key,
                    Name = dto.Name,
                    Description = dto.Description,
                    SensorType = dto.SensorType,
                    Target = dto.Target,
                    TargetKey = string.Empty,
                    ScopeType = dto.ScopeType,
                    IsInherited = dto.IsInherited,
                    SourceScope = dto.SourceScope,
                    WarningThreshold = dto.WarningThreshold,
                    CriticalThreshold = dto.CriticalThreshold,
                    ThresholdUnit = dto.ThresholdUnit,
                    Comparison = dto.Comparison,
                    Channels = dto.Channels,
                    Status = SensorStatus.Pending,
                    IsStale = true,
                    IntervalSeconds = dto.IntervalSeconds,
                    TimeoutSeconds = dto.TimeoutSeconds,
                    FailsBeforeAlert = dto.FailsBeforeAlert,
                    SustainSamples = dto.SustainSamples,
                    MinSeverityToAlert = dto.MinSeverityToAlert,
                    ScriptId = dto.ScriptId,
                    ScriptName = dto.ScriptName,
                    ChartJson = dto.ChartJson,
                    DisplayOrder = dto.DisplayOrder,
                    IsPinned = dto.IsPinned
                });
                continue;
            }

            foreach (var state in sensorStates)
            {
                var isStale = state.LastRunAt is null
                    || (now - state.LastRunAt.Value.ToUniversalTime()).TotalSeconds > staleAfter;

                items.Add(new SensorItemDto
                {
                    SensorId = definition.Id,
                    Key = definition.Key,
                    Name = definition.Name,
                    Description = definition.Description,
                    SensorType = definition.SensorType,
                    Target = definition.Target,
                    TargetKey = state.TargetKey,
                    ScopeType = definition.ScopeType,
                    IsInherited = definition.ScopeType != AppApprovalScopeType.Agent,
                    SourceScope = definition.ScopeType,
                    WarningThreshold = definition.WarningThreshold,
                    CriticalThreshold = definition.CriticalThreshold,
                    ThresholdUnit = definition.ThresholdUnit,
                    Comparison = definition.Comparison,
                    Channels = SensorChannelCatalog.Resolve(definition),
                    CurrentChannels = SensorJson.ParseChannelsValues(state.ChannelsJson),
                    PrimaryChannel = state.PrimaryChannel,
                    PrimaryValue = state.PrimaryValue,
                    Sparkline = SensorJson.ParseSparkline(state.SparklineJson)
                        .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value, StringComparer.OrdinalIgnoreCase),
                    Status = state.Status,
                    Severity = state.Severity,
                    Message = state.Message,
                    IsStale = isStale,
                    ExitCode = state.ExitCode,
                    DurationMs = state.DurationMs,
                    OutputExcerpt = state.OutputExcerpt,
                    ConsecutiveFailures = state.ConsecutiveFailures,
                    TotalRuns = state.TotalRuns,
                    TotalFailures = state.TotalFailures,
                    LastRunAt = state.LastRunAt,
                    LastStatusChangedAt = state.LastStatusChangedAt,
                    LastAlertedAt = state.LastAlertedAt,
                    IntervalSeconds = interval,
                    TimeoutSeconds = definition.TimeoutSeconds,
                    FailsBeforeAlert = definition.FailsBeforeAlert,
                    SustainSamples = definition.SustainSamples,
                    MinSeverityToAlert = definition.MinSeverityToAlert,
                    ScriptId = definition.ScriptId,
                    ScriptName = definition.ScriptId is Guid sid && scriptNames.TryGetValue(sid, out var name) ? name : null,
                    ChartJson = definition.ChartJson,
                    DisplayOrder = definition.DisplayOrder,
                    IsPinned = definition.IsPinned
                });
            }
        }

        items = items
            .OrderBy(i => i.DisplayOrder)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.TargetKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AgentSensorsPayloadDto
        {
            AgentId = agent.Id,
            SensorFingerprint = fingerprint,
            LastSyncedSensorFingerprint = agent.LastSensorFingerprint,
            LastSensorSyncAt = agent.LastSensorSyncAt,
            UpToDate = agent.LastSensorFingerprint is not null
                && string.Equals(agent.LastSensorFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase),
            GeneratedAt = now,
            DefaultIntervalSeconds = settings.EffectiveDefaultIntervalSeconds,
            StaleAfterSeconds = settings.EffectiveDefaultIntervalSeconds * SensorDefaults.StaleMultiplier,
            // Contadores por STATUS (todas as linhas) + Stale como dimensão
            // separada (linhas sem dado recente). Somar os status = Total; Stale
            // é sobreposição, não categoria.
            Counts = new SensorCountsDto
            {
                Total = items.Count,
                Passing = items.Count(i => i.Status == SensorStatus.Passing),
                Warning = items.Count(i => i.Status == SensorStatus.Warning),
                Failing = items.Count(i => i.Status == SensorStatus.Failing),
                Pending = items.Count(i => i.Status == SensorStatus.Pending),
                Unknown = items.Count(i => i.Status == SensorStatus.Unknown),
                Stale = items.Count(i => i.IsStale)
            },
            Items = items
        };
    }

    public async Task<SensorFleetSummaryDto> GetFleetSummaryAsync(Guid? clientId, Guid? siteId, CancellationToken cancellationToken = default)
    {
        var settings = await SensorSettingsStore.LoadAsync(db, cancellationToken);
        var now = DateTime.UtcNow;

        var intervalBySensor = await db.SensorDefinitions.AsNoTracking()
            .Select(s => new { s.Id, s.IntervalSeconds })
            .ToDictionaryAsync(s => s.Id, s => s.IntervalSeconds > 0
                ? Math.Clamp(s.IntervalSeconds, SensorDefaults.MinIntervalSeconds, SensorDefaults.MaxIntervalSeconds)
                : settings.EffectiveDefaultIntervalSeconds, cancellationToken);

        var query =
            from state in db.AgentSensorStates.AsNoTracking()
            join agent in db.Agents.AsNoTracking() on state.AgentId equals agent.Id
            join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            where agent.DeletedAt == null
            select new { state, agent, site };

        if (siteId is not null) query = query.Where(x => x.agent.SiteId == siteId);
        if (clientId is not null) query = query.Where(x => x.site.ClientId == clientId);

        var rows = await query
            .Select(x => new
            {
                x.state.AgentId,
                x.state.SensorId,
                x.state.Status,
                x.state.Severity,
                x.state.LastRunAt,
                x.agent.Hostname,
                x.agent.DisplayName,
                x.site.ClientId,
                SiteId = x.agent.SiteId,
                SiteName = x.site.Name
            })
            .ToListAsync(cancellationToken);

        var definitions = await db.SensorDefinitions.AsNoTracking()
            .Where(s => s.DeletedAt == null)
            .Select(s => new { s.Id, s.SensorType })
            .ToDictionaryAsync(s => s.Id, s => s.SensorType, cancellationToken);

        var counts = new SensorFleetCountsDto { Total = rows.Count };
        var failingAgents = new HashSet<Guid>();

        foreach (var row in rows)
        {
            var isStale = row.LastRunAt is null
                || (now - row.LastRunAt.Value.ToUniversalTime()).TotalSeconds
                   > Math.Max(intervalBySensor.GetValueOrDefault(row.SensorId, settings.EffectiveDefaultIntervalSeconds)
                              * SensorDefaults.StaleMultiplier, 120);

            // Stale é sobreposição (dado antigo), não categoria de status.
            if (isStale) counts.Stale++;

            switch (row.Status)
            {
                case SensorStatus.Passing: counts.Passing++; break;
                case SensorStatus.Warning: counts.Warning++; break;
                case SensorStatus.Failing: counts.Failing++; break;
                case SensorStatus.Unknown: counts.Unknown++; break;
                default: counts.Pending++; break;
            }

            if (row.Status == SensorStatus.Failing) failingAgents.Add(row.AgentId);
        }

        counts.AgentsWithFailing = failingAgents.Count;

        var byType = rows
            .GroupBy(row => definitions.GetValueOrDefault(row.SensorId, SensorType.Script))
            .Select(group => new SensorFleetByTypeDto
            {
                SensorType = group.Key,
                Total = group.Count(),
                Warning = group.Count(r => r.Status == SensorStatus.Warning),
                Failing = group.Count(r => r.Status == SensorStatus.Failing)
            })
            .OrderByDescending(item => item.Failing)
            .ThenByDescending(item => item.Warning)
            .ThenBy(item => item.SensorType)
            .ToList();

        var clientNames = await db.Clients.AsNoTracking()
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var top = rows
            .GroupBy(row => row.AgentId)
            .Select(group => new
            {
                AgentId = group.Key,
                Failing = group.Count(r => r.Status == SensorStatus.Failing),
                Warning = group.Count(r => r.Status == SensorStatus.Warning),
                Worst = group
                    .Where(r => r.Severity is not null)
                    .Select(r => r.Severity!.Value)
                    .DefaultIfEmpty(SensorSeverity.Info)
                    .Max(),
                First = group.First()
            })
            .Where(item => item.Failing > 0 || item.Warning > 0)
            .OrderByDescending(item => item.Failing)
            .ThenByDescending(item => item.Warning)
            .Take(10)
            .Select(item => new SensorFleetAgentDto
            {
                AgentId = item.AgentId,
                Hostname = item.First.Hostname,
                DisplayName = item.First.DisplayName,
                ClientId = item.First.ClientId,
                ClientName = clientNames.GetValueOrDefault(item.First.ClientId),
                SiteId = item.First.SiteId,
                SiteName = item.First.SiteName,
                Failing = item.Failing,
                Warning = item.Warning,
                WorstSeverity = item.Failing > 0 ? SensorSeverity.Critical
                    : (item.Warning > 0 ? SensorSeverity.Warning : (SensorSeverity?)null)
            })
            .ToList();

        return new SensorFleetSummaryDto
        {
            GeneratedAt = now,
            ClientId = clientId,
            SiteId = siteId,
            Counts = counts,
            ByType = byType,
            Top = top
        };
    }

    private async Task<Dictionary<Guid, string>> LoadScriptNamesAsync(
        IReadOnlyList<SensorDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var ids = definitions.Where(s => s.ScriptId.HasValue).Select(s => s.ScriptId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await db.AutomationScriptDefinitions.AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => $"{s.Name} v{s.Version}", cancellationToken);
    }
}
