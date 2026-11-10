using System.Text;
using Discovery.Core.Cqrs;
using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Repositories;
using MediatR;

namespace Discovery.Infrastructure.Cqrs.Patches;

// ── Helpers ────────────────────────────────────────────────────────────────

/// <summary>
/// Filtro de ORIGEM dos updates do agente: "windows" = so patches do SO
/// (esconde choco/winget), "third-party" = so apps de terceiros, null/"" =
/// tudo. Fonte unica para a lista paginada e os contadores do snapshot.
/// </summary>
internal static class PatchSourceFilter
{
    private static readonly string[] ThirdPartySources = ["choco", "winget"];

    public static bool IsThirdParty(string? source) =>
        source is not null &&
        ThirdPartySources.Contains(source.Trim(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AgentWindowsUpdate> Apply(
        IReadOnlyList<AgentWindowsUpdate> updates,
        string? source)
    {
        var normalized = source?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "windows" => updates.Where(update => !IsThirdParty(update.Source)).ToList(),
            "third-party" => updates.Where(update => IsThirdParty(update.Source)).ToList(),
            _ => updates
        };
    }
}

internal static class PatchScopeResolver
{
    public static (Guid? ClientId, Guid? SiteId) Resolve(PatchScope scope, Guid? scopeId) => scope switch
    {
        PatchScope.Client => (scopeId, null),
        PatchScope.Site => (null, scopeId),
        _ => (null, null)
    };

    public static PatchUpdateStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "pending" => PatchUpdateStatus.Pending,
        "installed" => PatchUpdateStatus.Installed,
        "approved" => PatchUpdateStatus.Approved,
        "denied" => PatchUpdateStatus.Denied,
        "installing" => PatchUpdateStatus.Installing,
        "failed" => PatchUpdateStatus.Failed,
        "superseded" => PatchUpdateStatus.Superseded,
        _ => null
    };

    public static string EncodeKeyCursor(string groupKey)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(groupKey));
}

// ── Agente ────────────────────────────────────────────────────────────────

public sealed class GetAgentWindowsUpdatesPageQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetAgentWindowsUpdatesPageQuery, Result<AgentWindowsUpdatePageDto>>
{
    public async Task<Result<AgentWindowsUpdatePageDto>> Handle(
        GetAgentWindowsUpdatesPageQuery query,
        CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        // Mesmo teto do repositório (PatchRepository.MaxPageSize): a resposta
        // devolve o pageSize efetivo, nunca um limite que o Take não aplica.
        var pageSize = Math.Clamp(query.PageSize, 1, PatchRepository.MaxPageSize);
        var status = PatchScopeResolver.ParseStatus(query.Status);

        var result = await repo.GetAgentUpdatesPageAsync(
            query.AgentId, page, pageSize, query.Search, status, query.Severity, query.Descending, query.Source, ct);

        var dto = new AgentWindowsUpdatePageDto(
            result.Items.Select(PatchDtoMapper.ToDto).ToList(),
            result.TotalCount,
            page,
            pageSize,
            result.PendingCount,
            result.InstalledCount);

        return Result<AgentWindowsUpdatePageDto>.Success(dto);
    }
}

public sealed class GetAgentWindowsUpdatesSnapshotQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetAgentWindowsUpdatesSnapshotQuery, Result<AgentWindowsUpdateSnapshotDto>>
{
    public async Task<Result<AgentWindowsUpdateSnapshotDto>> Handle(
        GetAgentWindowsUpdatesSnapshotQuery query,
        CancellationToken ct)
    {
        var snapshot = await repo.GetSnapshotAsync(query.AgentId, ct);
        var pending = await repo.GetPendingForAgentAsync(query.AgentId, ct);

        // Contadores acompanham o filtro de origem da lista: com "Só Windows
        // Update" a aba nao pode anunciar 12 pendentes enquanto a tabela mostra
        // 1 (os apps de terceiros sao contados apenas quando visiveis).
        pending = PatchSourceFilter.Apply(pending, query.Source);

        var denied = pending.Count(update => update.ApprovalState == PatchApprovalState.Denied);
        var approved = pending.Count(update =>
            update.ApprovalState is PatchApprovalState.Approved or PatchApprovalState.AutoApproved);

        var dto = PatchDtoMapper.ToSnapshotDto(
            query.AgentId,
            snapshot,
            pending.Count,
            snapshot?.InstalledCount ?? 0,
            denied,
            approved);

        return Result<AgentWindowsUpdateSnapshotDto>.Success(dto with
        {
            DeniedCount = denied,
            ApprovedCount = approved
        });
    }
}

// ── Frota ─────────────────────────────────────────────────────────────────

public sealed class GetPatchInventoryQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchInventoryQuery, Result<PatchInventoryListDto>>
{
    public async Task<Result<PatchInventoryListDto>> Handle(GetPatchInventoryQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);

        var items = await repo.GetInventoryGroupsAsync(
            clientId, siteId, query.Cursor, limit + 1, query.Search, query.Severity, query.Status,
            query.Descending, query.HasGlobalAccess, query.AllowedClientIds, ct);

        var hasMore = items.Count > limit;
        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
            nextCursor = PatchScopeResolver.EncodeKeyCursor(items[^1].GroupKey);

        var dtos = items.Select(item => new PatchInventoryGroupDto(
            item.GroupKey,
            PatchJson.ParseStringArray(item.KbIds),
            item.Title,
            item.Classification,
            item.Severity,
            item.PublishedAt,
            item.PendingAgents,
            item.InstalledAgents,
            item.DeniedAgents,
            item.ReportingAgents,
            item.FirstSeenAt,
            item.LastSeenAt)).ToList();

        return Result<PatchInventoryListDto>.Success(new PatchInventoryListDto(dtos, nextCursor, hasMore));
    }
}

public sealed class GetPatchInventoryAgentsQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchInventoryAgentsQuery, Result<PatchInventoryAgentsDto>>
{
    public async Task<Result<PatchInventoryAgentsDto>> Handle(
        GetPatchInventoryAgentsQuery query,
        CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);
        var status = PatchScopeResolver.ParseStatus(query.Status);

        var items = await repo.GetInventoryAgentsAsync(
            query.GroupKey, clientId, siteId, query.Cursor, limit + 1, status,
            query.HasGlobalAccess, query.AllowedClientIds, ct);

        var hasMore = items.Count > limit;
        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
            nextCursor = Discovery.Core.Helpers.CursorPaginationHelper.EncodeGuidCursor(items[^1].AgentId);

        var dtos = items.Select(item => new PatchInventoryAgentDto(
            item.AgentId,
            item.UpdateKey,
            item.Hostname,
            item.AgentDisplayName,
            item.SiteId,
            item.SiteName,
            item.ClientId,
            item.ClientName,
            item.Status,
            item.ApprovalState,
            item.Severity,
            item.RequiresReboot,
            item.ReportedAt,
            item.LastSeenAt)).ToList();

        return Result<PatchInventoryAgentsDto>.Success(new PatchInventoryAgentsDto(dtos, nextCursor, hasMore));
    }
}

public sealed class GetPatchPendingQueryHandler(IPatchRepository repo, IHeartbeatCacheService heartbeatCache)
    : IRequestHandler<GetPatchPendingQuery, Result<PatchPendingListDto>>
{
    /// <summary>
    /// Orçamento da leitura ao vivo do Redis. Um Redis lento não pode segurar a
    /// lista: estourou o prazo, seguimos com o estado do Postgres (degradação,
    /// nunca erro) — mesmo padrão da busca universal.
    /// </summary>
    private static readonly TimeSpan LiveHeartbeatBudget = TimeSpan.FromMilliseconds(750);

    public async Task<Result<PatchPendingListDto>> Handle(GetPatchPendingQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);

        var items = await repo.GetPendingAgentsAsync(
            clientId, siteId, query.Cursor, limit + 1, query.Search,
            query.HasGlobalAccess, query.AllowedClientIds, ct);

        var hasMore = items.Count > limit;
        if (hasMore)
            items = items.Take(limit).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
            nextCursor = Discovery.Core.Helpers.CursorPaginationHelper.EncodeGuidCursor(items[^1].AgentId);

        var agentIds = items.Select(item => item.AgentId).ToList();

        // POR QUE buscar o estado em separado: a query de pendências agrupa em SQL
        // (GroupBy por agente) e o group key não carrega as colunas de estado —
        // incluí-las custaria colunas a mais no agrupamento. Uma única chamada para
        // a página inteira, já cortada em limit.
        //
        // POR QUE o Redis entra aqui: agents.status e agents.last_seen_at só são
        // escritos nas TRANSIÇÕES de heartbeat — um agente online há horas mantém o
        // instante do INÍCIO da sessão. A existência da chave heartbeat:agent:{id}
        // (TTL 180s) é a fonte de verdade de "online agora".
        var liveStates = await repo.GetAgentStatusesAsync(agentIds, ct);
        var heartbeats = await ReadLiveHeartbeatsAsync(agentIds, ct);

        var dtos = items.Select(item =>
        {
            var live = liveStates.GetValueOrDefault(item.AgentId);
            heartbeats.TryGetValue(item.AgentId, out var heartbeat);

            // Online se QUALQUER fonte afirmar: chave viva no Redis OU status
            // persistido Online. Assim a página fica correta tanto quando o
            // Postgres está defasado quanto quando o Redis está indisponível.
            var isOnline = heartbeat is not null || live?.Status == AgentStatus.Online;

            // Frescor: instante REAL do último heartbeat (Redis) → agents.last_seen_at.
            // O último fallback é defensivo: a linha de pendências já projeta
            // agents.last_seen_at, mas um agente fora do dicionário não pode zerar
            // o frescor que veio do SQL.
            var lastSeenAt = heartbeat?.LastHeartbeatAt ?? live?.LastSeenAt ?? item.LastSeenAt;

            return new PatchPendingAgentDto(
                item.AgentId,
                item.Hostname,
                item.AgentDisplayName,
                item.SiteId,
                item.SiteName,
                item.ClientId,
                item.ClientName,
                item.PendingCount,
                item.CriticalCount,
                item.DeniedCount,
                item.PendingReboot,
                item.ScanStatus,
                item.LastScanAt,
                lastSeenAt,
                (isOnline ? AgentStatus.Online : AgentStatus.Offline).ToString());
        }).ToList();

        return Result<PatchPendingListDto>.Success(new PatchPendingListDto(dtos, nextCursor, hasMore));
    }

    /// <summary>
    /// Heartbeats vivos (Redis) restritos aos ids da página. Falha, timeout ou
    /// Redis fora do ar devolvem dicionário vazio: a lista cai para o estado
    /// persistido sem quebrar nem esperar.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, HeartbeatCacheEntry>> ReadLiveHeartbeatsAsync(
        IReadOnlyCollection<Guid> agentIds, CancellationToken ct)
    {
        if (agentIds.Count == 0)
            return new Dictionary<Guid, HeartbeatCacheEntry>();

        try
        {
            using var liveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            liveCts.CancelAfter(LiveHeartbeatBudget);

            // Leituras da CHAVE INDIVIDUAL de cada agente da página, numa única
            // rodada concorrente. NÃO usamos a varredura por prefixo: o conjunto
            // heartbeat:online:agents está vazio em produção e o SCAN não é
            // confiável como fonte de "quem está online".
            return await heartbeatCache.GetHeartbeatsAsync(agentIds, liveCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Orçamento estourado — degrada para o estado persistido.
            return new Dictionary<Guid, HeartbeatCacheEntry>();
        }
        catch
        {
            // Redis indisponível é degradação, não erro da lista.
            return new Dictionary<Guid, HeartbeatCacheEntry>();
        }
    }
}

public sealed class GetPatchSnapshotQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchSnapshotQuery, Result<PatchScopeSnapshotDto>>
{
    public async Task<Result<PatchScopeSnapshotDto>> Handle(GetPatchSnapshotQuery query, CancellationToken ct)
    {
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);
        var snapshot = await repo.GetScopeSnapshotAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, ct);

        return Result<PatchScopeSnapshotDto>.Success(ToDto(snapshot));
    }

    internal static PatchScopeSnapshotDto ToDto(PatchScopeSnapshot snapshot)
    {
        var compliance = 0.0;
        if (snapshot.AgentsReporting > 0)
        {
            var compliant = Math.Max(0, snapshot.AgentsReporting - snapshot.AgentsWithPending);
            compliance = Math.Round(100.0 * compliant / snapshot.AgentsReporting, 1);
        }

        return new PatchScopeSnapshotDto(
            snapshot.TotalPending,
            snapshot.CriticalPending,
            snapshot.DeniedPending,
            snapshot.ApprovedPending,
            snapshot.AgentsWithPending,
            snapshot.AgentsTotal,
            snapshot.AgentsReporting,
            snapshot.RebootPendingAgents,
            snapshot.FailedInstallations24h,
            compliance,
            snapshot.LastScanAt);
    }
}

public sealed class GetPatchOverviewQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchOverviewQuery, Result<PatchOverviewDto>>
{
    public async Task<Result<PatchOverviewDto>> Handle(GetPatchOverviewQuery query, CancellationToken ct)
    {
        var (clientId, siteId) = PatchScopeResolver.Resolve(query.Scope, query.ScopeId);

        var snapshot = await repo.GetScopeSnapshotAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, ct);
        var bySeverity = await repo.GetPendingBySeverityAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, 10, ct);
        var byClassification = await repo.GetPendingByClassificationAsync(
            clientId, siteId, query.HasGlobalAccess, query.AllowedClientIds, 10, ct);

        var dto = new PatchOverviewDto(
            GetPatchSnapshotQueryHandler.ToDto(snapshot),
            bySeverity.Select(item => new PatchSeverityCountDto(item.Label, item.Count)).ToList(),
            byClassification.Select(item => new PatchSeverityCountDto(item.Label, item.Count)).ToList());

        return Result<PatchOverviewDto>.Success(dto);
    }
}

// ── Aprovações e política ─────────────────────────────────────────────────

public sealed class ListPatchApprovalsQueryHandler(IPatchRepository repo)
    : IRequestHandler<ListPatchApprovalsQuery, Result<IReadOnlyList<PatchApprovalDto>>>
{
    public async Task<Result<IReadOnlyList<PatchApprovalDto>>> Handle(
        ListPatchApprovalsQuery query,
        CancellationToken ct)
    {
        var items = await repo.GetApprovalsAsync(
            query.ScopeType, query.ScopeId, query.Search, Math.Clamp(query.Limit, 1, 500), ct);

        return Result<IReadOnlyList<PatchApprovalDto>>.Success(
            items.Select(PatchDtoMapper.ToDto).ToList());
    }
}

public sealed class ListPatchApprovalAuditQueryHandler(IPatchRepository repo)
    : IRequestHandler<ListPatchApprovalAuditQuery, Result<IReadOnlyList<PatchApprovalAuditDto>>>
{
    public async Task<Result<IReadOnlyList<PatchApprovalAuditDto>>> Handle(
        ListPatchApprovalAuditQuery query,
        CancellationToken ct)
    {
        var items = await repo.GetApprovalAuditAsync(Math.Clamp(query.Limit, 1, 500), ct);
        return Result<IReadOnlyList<PatchApprovalAuditDto>>.Success(
            items.Select(PatchDtoMapper.ToDto).ToList());
    }
}

public sealed class GetPatchPolicyQueryHandler(IPatchRepository repo)
    : IRequestHandler<GetPatchPolicyQuery, Result<PatchPolicyDto>>
{
    public async Task<Result<PatchPolicyDto>> Handle(GetPatchPolicyQuery query, CancellationToken ct)
    {
        var policy = await repo.GetPolicyAsync(query.ScopeType, query.ScopeId, ct);
        if (policy is null)
        {
            // Devolve os defaults conservadores (Monitor, sem auto-install, reboot Never).
            policy = new PatchPolicy
            {
                Id = Guid.Empty,
                ScopeType = query.ScopeType,
                ScopeId = query.ScopeId,
                EnforcementMode = PatchEnforcementMode.Monitor,
                RebootPolicy = PatchRebootPolicy.Never,
                MaxInstallsPerRun = 50
            };
        }

        return Result<PatchPolicyDto>.Success(PatchDtoMapper.ToDto(policy));
    }
}

public sealed class ListPatchPoliciesQueryHandler(IPatchRepository repo)
    : IRequestHandler<ListPatchPoliciesQuery, Result<IReadOnlyList<PatchPolicyDto>>>
{
    public async Task<Result<IReadOnlyList<PatchPolicyDto>>> Handle(
        ListPatchPoliciesQuery query,
        CancellationToken ct)
    {
        var items = await repo.GetPoliciesAsync(ct);
        return Result<IReadOnlyList<PatchPolicyDto>>.Success(
            items.Select(PatchDtoMapper.ToDto).ToList());
    }
}
