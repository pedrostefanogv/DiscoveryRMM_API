using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;

internal static class AgentQueryHelper
{
    internal const int MinimumLiveMetricsGraceSeconds = 60;

    internal static AgentDto MapToDto(Agent a) => new(
        a.Id,
        a.Hostname,
        a.DisplayName,
        Guid.Empty,
        a.SiteId,
        a.EffectiveStatus.ToString(),
        a.OperatingSystem,
        a.OsVersion,
        AgentVersionNormalizer.PickVersion(a.AgentVersion),
        AgentVersionNormalizer.NormalizeCommit(a.CommitHash),
        a.MacAddress,
        a.LastIpAddress,
        a.EffectiveStatus == AgentStatus.Online,
        a.LastSeenAt,
        a.ZeroTouchPending,
        a.CreatedAt,
        a.UpdatedAt,
        null);

    internal static AgentDto MapToDto(Agent a, HeartbeatCacheEntry? hb)
    {
        // Versão: o heartbeat é a fonte mais fresca (reflete o binário em
        // execução, inclusive logo após um self-update), mas placeholders de
        // build local ("0.0.0"/"dev") não podem ofuscar a versão real já
        // persistida pelo hardware report — daí o PickVersion. O commit não
        // viaja no heartbeat (dado estático, atualiza via inventário), então
        // vem sempre da entidade, normalizado.
        var version = AgentVersionNormalizer.PickVersion(hb?.AgentVersion, a.AgentVersion);
        var commitHash = AgentVersionNormalizer.NormalizeCommit(a.CommitHash);

        return new AgentDto(
            a.Id,
            hb?.Hostname ?? a.Hostname,
            a.DisplayName,
            Guid.Empty,
            a.SiteId,
            a.EffectiveStatus.ToString(),
            a.OperatingSystem,
            a.OsVersion,
            version,
            commitHash,
            a.MacAddress,
            hb?.IpAddress ?? a.LastIpAddress,
            a.EffectiveStatus == AgentStatus.Online,
            hb?.LastHeartbeatAt ?? a.LastSeenAt,
            a.ZeroTouchPending,
            a.CreatedAt,
            a.UpdatedAt,
            hb is null ? null : new HeartbeatMetricsDto(
                hb.CpuPercent, hb.CpuTemperatureCelsius, hb.MemoryPercent, hb.DiskPercent,
                hb.MemoryTotalGb, hb.MemoryUsedGb, hb.DiskTotalGb, hb.DiskUsedGb,
                hb.DiskReadPercent, hb.DiskWritePercent, hb.DiskResponseMs,
                hb.P2pPeers, hb.UptimeSeconds, hb.ProcessCount,
                hb.UiOnline,
                hb.IpAddress, hb.Hostname, version, commitHash,
                hb.LastHeartbeatAt, hb.LastHeartbeatAt));
    }

    internal static void ApplyRealtimeHeartbeat(Agent agent, HeartbeatCacheEntry? heartbeat)
    {
        if (heartbeat is null || agent.MaintenanceEnabled) return;
        agent.Status = AgentStatus.Online;
        agent.LastSeenAt = heartbeat.LastHeartbeatAt;
    }

    internal static void ApplyEffectiveStatus(Agent agent, int onlineGraceSeconds)
    {
        if (agent.MaintenanceEnabled) { agent.Status = AgentStatus.Maintenance; return; }
        if (agent.Status != AgentStatus.Online) return;
        var cutoffUtc = DateTime.UtcNow.AddSeconds(-Math.Max(MinimumLiveMetricsGraceSeconds, onlineGraceSeconds));
        if (!agent.LastSeenAt.HasValue || agent.LastSeenAt.Value < cutoffUtc)
            agent.Status = AgentStatus.Offline;
    }

    internal static async Task<Dictionary<Guid, HeartbeatCacheEntry>> GetHeartbeatSnapshotAsync(IHeartbeatCacheService heartbeatCache, IEnumerable<Guid> agentIds)
    {
        var ids = agentIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var results = await Task.WhenAll(ids.Select(async id => new { AgentId = id, Entry = await heartbeatCache.GetHeartbeatAsync(id) }));
        return results.Where(x => x.Entry is not null).ToDictionary(x => x.AgentId, x => x.Entry!);
    }

    internal static async Task<int> GetOnlineGraceSecondsAsync(IConfigurationResolver configResolver, Guid siteId)
    {
        try { var r = await configResolver.ResolveForSiteAsync(siteId); return Math.Max(MinimumLiveMetricsGraceSeconds, r.AgentOnlineGraceSeconds); }
        catch { return MinimumLiveMetricsGraceSeconds; }
    }

    internal static async Task<Dictionary<Guid, int>> GetOnlineGraceSecondsBySiteAsync(IConfigurationResolver configResolver, IEnumerable<Guid> siteIds)
    {
        var ids = siteIds.Distinct().ToList();
        var tasks = ids.Select(async siteId =>
        {
            try { var r = await configResolver.ResolveForSiteAsync(siteId); return (siteId, grace: Math.Max(MinimumLiveMetricsGraceSeconds, r.AgentOnlineGraceSeconds)); }
            catch { return (siteId, grace: MinimumLiveMetricsGraceSeconds); }
        });
        return (await Task.WhenAll(tasks)).ToDictionary(e => e.siteId, e => e.grace);
    }
}