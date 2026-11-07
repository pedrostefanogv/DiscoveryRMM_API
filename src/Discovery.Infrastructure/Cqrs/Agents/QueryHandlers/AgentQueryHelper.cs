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
        null,
        LoggedUser: a.LoggedUser,
        PendingReboot: a.PendingReboot,
        PendingRebootReasons: a.PendingRebootReasons,
        PendingRebootCheckedAt: a.PendingRebootCheckedAt);

    /// <summary>
    /// Identidade Windows Server por agente, vinda de <c>agent_hardware_info</c>
    /// (colunas <c>os_is_server</c>/<c>os_product_type</c>, migration M272).
    /// Valores <c>null</c> significam "não reportado" — nunca "não é servidor".
    /// </summary>
    internal readonly record struct AgentOsIdentity(bool? IsServer, string? ProductType);

    /// <summary>
    /// Busca a identidade de SO em LOTE para os agentes pedidos.
    ///
    /// Feito à parte do MapToDto de propósito: a listagem de agentes é chamada
    /// com frequência e um Include/join por agente transformaria a consulta em
    /// N+1. Aqui é UMA consulta projetando só as duas colunas, devolvendo um
    /// dicionário vazio quando o repositório não suporta (o DTO fica com null,
    /// que o site trata como "desconhecido" e cai para a heurística).
    /// </summary>
    internal static async Task<Dictionary<Guid, AgentOsIdentity>> GetOsIdentityByAgentAsync(
        IAgentHardwareRepository hardwareRepo,
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct)
    {
        var map = new Dictionary<Guid, AgentOsIdentity>(agentIds.Count);
        if (agentIds.Count == 0)
            return map;

        // UMA consulta para todos os agentes da página — não um por agente.
        // Se um agente ainda não enviou hardware, ele simplesmente não aparece
        // no dicionário e o DTO fica com null (desconhecido, não "cliente").
        var hardwareById = await hardwareRepo.GetByAgentIdsAsync(agentIds, ct);
        foreach (var (agentId, hardware) in hardwareById)
        {
            if (hardware is null)
                continue;
            map[agentId] = new AgentOsIdentity(hardware.IsServer, hardware.ProductType);
        }

        return map;
    }

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

        var dto = new AgentDto(
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
                hb.LastHeartbeatAt, hb.LastHeartbeatAt),
            PendingReboot: a.PendingReboot,
            PendingRebootReasons: a.PendingRebootReasons,
            PendingRebootCheckedAt: a.PendingRebootCheckedAt);

        return WithLiveLoggedUser(dto, a, hb);
    }

    /// <summary>
    /// MapToDto com a identidade de SO já resolvida (ver <see cref="AgentOsIdentity"/>).
    /// Sobrecarga separada para NÃO obrigar todos os chamadores existentes a
    /// carregar hardware só para montar o DTO.
    /// </summary>
    internal static AgentDto MapToDto(Agent a, HeartbeatCacheEntry? hb, AgentOsIdentity identity)
        => MapToDto(a, hb) with { IsServer = identity.IsServer, ProductType = identity.ProductType };

    /// <summary>
    /// Precedência do usuário logado: heartbeat ao vivo (Redis) → último
    /// persistido. Vazio ("") é valor válido do agent novo SEM sessão e não pode
    /// cair para o persistido; null significa "agent não reporta o campo".
    /// </summary>
    internal static AgentDto WithLiveLoggedUser(AgentDto dto, Agent a, HeartbeatCacheEntry? heartbeat)
        => dto with
        {
            LoggedUser = heartbeat?.LoggedUser ?? a.LoggedUser,
            LoggedUserSince = heartbeat?.LoggedUserSince
        };

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