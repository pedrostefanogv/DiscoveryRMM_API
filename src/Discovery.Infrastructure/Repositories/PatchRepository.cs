using Discovery.Core.Cqrs.Patches;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

/// <summary>
/// Repositório do gerenciador de patches (Windows Update).
///
/// Regras de escopo: todas as consultas de frota recebem a access list do
/// usuário (HasGlobalAccess + AllowedClientIds) e filtram no SQL — nunca
/// confiar em scopeId vindo da query string.
/// </summary>
public sealed class PatchRepository(DiscoveryDbContext db) : IPatchRepository
{
    private const int MaxPageSize = 200;
    private const int MaxLimit = 500;

    // ── Agente ──────────────────────────────────────────────────────────────

    public async Task<AgentWindowsUpdatePageResult> GetAgentUpdatesPageAsync(
        Guid agentId,
        int page,
        int pageSize,
        string? search,
        PatchUpdateStatus? status,
        string? severity,
        bool descending,
        CancellationToken ct = default)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 2000);

        var query = db.AgentWindowsUpdates
            .AsNoTracking()
            .Where(update => update.AgentId == agentId && update.IsPresent);

        if (status.HasValue)
            query = query.Where(update => update.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(severity))
        {
            var severityValue = severity.Trim();
            query = query.Where(update => update.Severity != null &&
                EF.Functions.ILike(update.Severity, severityValue));
        }

        var patterns = SoftwareSearchTerms.BuildPatterns(search);
        if (patterns is not null)
        {
            foreach (var pattern in patterns)
            {
                var term = pattern;
                query = query.Where(update =>
                    EF.Functions.ILike(update.Title, term, SoftwareSearchTerms.EscapeCharacter) ||
                    EF.Functions.ILike(update.UpdateKey, term, SoftwareSearchTerms.EscapeCharacter) ||
                    EF.Functions.ILike(update.GroupKey, term, SoftwareSearchTerms.EscapeCharacter) ||
                    (update.Classification != null &&
                     EF.Functions.ILike(update.Classification, term, SoftwareSearchTerms.EscapeCharacter)));
            }
        }

        var totalCount = await query.CountAsync(ct);
        var pendingCount = await query.CountAsync(update => update.IsPending, ct);
        var installedCount = await query.CountAsync(update => update.IsInstalled, ct);

        var ordered = descending
            ? query.OrderByDescending(update => update.IsPending)
                .ThenByDescending(update => update.Title)
                .ThenByDescending(update => update.Id)
            : query.OrderByDescending(update => update.IsPending)
                .ThenBy(update => update.Title)
                .ThenBy(update => update.Id);

        var items = await ordered
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(ct);

        return new AgentWindowsUpdatePageResult
        {
            Items = items,
            TotalCount = totalCount,
            PendingCount = pendingCount,
            InstalledCount = installedCount
        };
    }

    public Task<AgentWindowsUpdateSnapshot?> GetSnapshotAsync(Guid agentId, CancellationToken ct = default)
        => db.AgentWindowsUpdateSnapshots.AsNoTracking().FirstOrDefaultAsync(snapshot => snapshot.AgentId == agentId, ct);

    public Task<AgentWindowsUpdate?> GetAgentUpdateAsync(Guid agentId, string updateKey, CancellationToken ct = default)
        => db.AgentWindowsUpdates
            .FirstOrDefaultAsync(update => update.AgentId == agentId &&
                update.IsPresent &&
                (update.UpdateKey == updateKey || update.GroupKey == updateKey), ct);

    public async Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentAsync(Guid agentId, CancellationToken ct = default)
        => await db.AgentWindowsUpdates
            .AsNoTracking()
            .Where(update => update.AgentId == agentId && update.IsPresent && update.IsPending)
            .OrderBy(update => update.Title)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AgentWindowsUpdate>> GetPendingForAgentsAsync(
        IReadOnlyCollection<Guid> agentIds,
        CancellationToken ct = default)
    {
        if (agentIds.Count == 0)
            return [];
        return await db.AgentWindowsUpdates
            .AsNoTracking()
            .Where(update => agentIds.Contains(update.AgentId) && update.IsPresent && update.IsPending)
            .ToListAsync(ct);
    }

    public async Task ReplaceInventoryAsync(
        Guid agentId,
        DateTime collectedAt,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        IReadOnlyList<AgentWindowsUpdateEntry> updates,
        PatchServiceStateMeta? serviceState = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var reportedAt = collectedAt.Kind == DateTimeKind.Utc ? collectedAt : collectedAt.ToUniversalTime();

        var validEntries = updates
            .Where(entry => !string.IsNullOrWhiteSpace(entry.UpdateKey))
            .GroupBy(entry => entry.UpdateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (validEntries.Count == 0)
        {
            // Upload SEM lista: nada disso prova que as atualizações sumiram.
            // Zera contadores e marca Superseded? Não — o inventário bom fica.
            // Só os metadados do scan e a sonda de estado são atualizados.
            await UpdateScanStatusAsync(agentId, scan, pendingReboot, serviceState, ct);
            return;
        }

        // Marca tudo como ausente (será reativado o que veio no payload). Em
        // Postgres o bulk é 1 UPDATE; em providers sem ExecuteUpdate (InMemory dos
        // testes) cai para o update das próprias entidades — mesmo resultado.
        var existing = await db.AgentWindowsUpdates
            .Where(update => update.AgentId == agentId)
            .ToListAsync(ct);

        if (db.Database.IsRelational())
        {
            await db.AgentWindowsUpdates
                .Where(update => update.AgentId == agentId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(update => update.IsPresent, _ => false)
                    .SetProperty(update => update.UpdatedAt, _ => now), ct);
        }
        else
        {
            foreach (var update in existing)
            {
                update.IsPresent = false;
                update.UpdatedAt = now;
            }
        }

        var existingByKey = existing
            .GroupBy(update => update.UpdateKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in validEntries)
        {
            var groupKey = BuildGroupKey(entry);
            if (!existingByKey.TryGetValue(entry.UpdateKey, out var row))
            {
                row = new AgentWindowsUpdate
                {
                    Id = Guid.NewGuid(),
                    AgentId = agentId,
                    UpdateKey = entry.UpdateKey,
                    FirstSeenAt = reportedAt,
                    CreatedAt = now
                };
                db.AgentWindowsUpdates.Add(row);
                existingByKey[entry.UpdateKey] = row;
            }

            row.GroupKey = groupKey;
            row.KbIds = entry.KbIds;
            row.UpdateId = entry.UpdateId;
            row.Revision = entry.Revision;
            row.Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.UpdateKey : entry.Title;
            row.Description = entry.Description;
            row.Classification = entry.Classification;
            row.Categories = entry.Categories;
            row.Severity = entry.Severity;
            row.IsPending = entry.IsPending;
            row.IsInstalled = entry.IsInstalled;
            row.IsHidden = entry.IsHidden;
            row.IsDownloaded = entry.IsDownloaded;
            row.IsMandatory = entry.IsMandatory;
            row.RequiresReboot = entry.RequiresReboot;
            row.MaxDownloadSize = entry.MaxDownloadSize;
            row.SupportUrl = entry.SupportUrl;
            row.PublishedAt = entry.PublishedAt;
            row.LastDeployedAt = entry.LastDeployedAt;
            if (entry.InstalledAt.HasValue)
                row.InstalledAt ??= entry.InstalledAt;
            row.ReportedAt = reportedAt;
            row.LastSeenAt = reportedAt;
            row.IsPresent = true;
            row.Source = entry.Source;
            row.Raw = entry.Raw;
            row.UpdatedAt = now;

            // Instalada: status final (preserva código de erro para exibição).
            if (entry.IsInstalled)
            {
                row.Status = PatchUpdateStatus.Installed;
            }
            else if (entry.IsPending)
            {
                // Preserva "Installing" recente (a UI mostra o progresso) e
                // reclassifica aprovação depois (patch approval resolver).
                if (row.Status is not PatchUpdateStatus.Installing)
                    row.Status = PatchUpdateStatus.Pending;
            }
        }

        // Rows que sumiram: mantém histórico (is_present=false) e marca
        // Superseded quando estavam pendentes/aprovadas — evita "fantasma" na UI.
        // Exceção: a linha de instalação antiga (guid) do MESMO KB que agora veio
        // como instalada via QFE (kb:...) fica oculta para não duplicar a aba
        // "Instaladas".
        var installedGroupKeys = validEntries
            .Where(entry => entry.IsInstalled)
            .Select(BuildGroupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Scan parcial sem a lista de instaladas (QFE falhou): NÃO apagar as
        // linhas instaladas — o silêncio não é prova de remoção.
        var preserveInstalled = string.Equals(scan.Status, "partial", StringComparison.OrdinalIgnoreCase) &&
                                !validEntries.Any(entry => entry.IsInstalled);

        // Scan "partial" sem NENHUMA linha pendente: a busca WUA pode ter falhado
        // e a lista de pendentes veio vazia por erro. Marcar Superseded aqui
        // apagaria pendências reais da aba — preserva e mantém o contador.
        var preservePending = string.Equals(scan.Status, "partial", StringComparison.OrdinalIgnoreCase) &&
                              !validEntries.Any(entry => entry.IsPending);

        foreach (var row in existing)
        {
            if (row.IsPresent)
                continue;

            if (row.IsInstalled && preserveInstalled)
            {
                row.IsPresent = true;
                row.UpdatedAt = now;
                continue;
            }

            if (row.IsInstalled && installedGroupKeys.Contains(row.GroupKey))
            {
                row.IsPresent = false;
                row.UpdatedAt = now;
                continue;
            }

            if (!row.IsPending)
                continue;

            if (preservePending)
            {
                row.IsPresent = true;
                row.UpdatedAt = now;
                continue;
            }

            row.Status = PatchUpdateStatus.Superseded;
            row.UpdatedAt = now;
        }

        // Snapshot do scan (contadores de pendentes/instaladas; denied vem do resolver).
        var snapshot = await db.AgentWindowsUpdateSnapshots
            .FirstOrDefaultAsync(item => item.AgentId == agentId, ct);
        if (snapshot is null)
        {
            snapshot = new AgentWindowsUpdateSnapshot { AgentId = agentId };
            db.AgentWindowsUpdateSnapshots.Add(snapshot);
        }
        snapshot.CollectedAt = reportedAt;
        snapshot.ScanStatus = string.IsNullOrWhiteSpace(scan.Status) ? "ok" : scan.Status;
        snapshot.ScanDurationMs = scan.DurationMs;
        snapshot.ScanError = scan.ErrorCode;

        // Lista parcial (uma das fontes falhou): só aceita contadores que o
        // agente realmente viu — contagem zero de uma fonte que falhou nunca
        // sobrescreve o número conhecido (evita "0 pendentes" falso).
        var isPartial = string.Equals(snapshot.ScanStatus, "partial", StringComparison.OrdinalIgnoreCase);
        if (!isPartial || validEntries.Any(entry => entry.IsPending))
            snapshot.PendingCount = validEntries.Count(entry => entry.IsPending);
        if (!isPartial || validEntries.Any(entry => entry.IsInstalled))
            snapshot.InstalledCount = validEntries.Count(entry => entry.IsInstalled);

        if (pendingReboot is not null)
        {
            snapshot.PendingReboot = pendingReboot.Pending;
            snapshot.PendingRebootReasons = PatchJson.SerializeStringArray(pendingReboot.Reasons?.ToArray());
            snapshot.RebootCheckedAt = pendingReboot.CheckedAt?.ToUniversalTime() ?? now;
        }
        ApplyServiceState(snapshot, serviceState, now);
        snapshot.UpdatedAt = now;

        if (pendingReboot is not null)
            await ApplyAgentPendingRebootAsync(agentId, pendingReboot, now, ct);

        await db.SaveChangesAsync(ct);
    }

    public Task UpdateScanStatusAsync(
        Guid agentId,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        CancellationToken ct = default)
        => UpdateScanStatusAsync(agentId, scan, pendingReboot, null, ct);

    public async Task UpdateScanStatusAsync(
        Guid agentId,
        PatchScanMeta scan,
        PatchPendingRebootMeta? pendingReboot,
        PatchServiceStateMeta? serviceState,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var snapshot = await db.AgentWindowsUpdateSnapshots
            .FirstOrDefaultAsync(item => item.AgentId == agentId, ct);
        if (snapshot is null)
        {
            snapshot = new AgentWindowsUpdateSnapshot { AgentId = agentId };
            db.AgentWindowsUpdateSnapshots.Add(snapshot);
        }

        snapshot.ScanStatus = string.IsNullOrWhiteSpace(scan.Status) ? "error" : scan.Status;
        snapshot.ScanDurationMs = scan.DurationMs;
        snapshot.ScanError = scan.ErrorCode;
        ApplyServiceState(snapshot, serviceState, now);
        snapshot.UpdatedAt = now;

        if (pendingReboot is not null)
        {
            snapshot.PendingReboot = pendingReboot.Pending;
            snapshot.PendingRebootReasons = PatchJson.SerializeStringArray(pendingReboot.Reasons?.ToArray());
            snapshot.RebootCheckedAt = pendingReboot.CheckedAt?.ToUniversalTime() ?? now;
            await ApplyAgentPendingRebootAsync(agentId, pendingReboot, now, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateServiceStateAsync(
        Guid agentId,
        PatchServiceStateMeta state,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var snapshot = await db.AgentWindowsUpdateSnapshots
            .FirstOrDefaultAsync(item => item.AgentId == agentId, ct);
        if (snapshot is null)
        {
            snapshot = new AgentWindowsUpdateSnapshot { AgentId = agentId };
            db.AgentWindowsUpdateSnapshots.Add(snapshot);
        }

        ApplyServiceState(snapshot, state, now);
        snapshot.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAgentPendingRebootAsync(
        Guid agentId,
        bool pending,
        IReadOnlyList<string> reasons,
        DateTime checkedAt,
        CancellationToken ct = default)
    {
        var meta = new PatchPendingRebootMeta
        {
            Pending = pending,
            Reasons = reasons,
            CheckedAt = checkedAt
        };
        await ApplyAgentPendingRebootAsync(agentId, meta, DateTime.UtcNow, ct);

        var snapshot = await db.AgentWindowsUpdateSnapshots
            .FirstOrDefaultAsync(item => item.AgentId == agentId, ct);
        if (snapshot is not null)
        {
            snapshot.PendingReboot = pending;
            snapshot.PendingRebootReasons = PatchJson.SerializeStringArray(reasons.ToArray());
            snapshot.RebootCheckedAt = checkedAt.Kind == DateTimeKind.Utc ? checkedAt : checkedAt.ToUniversalTime();
            snapshot.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Grava a sonda de estado (bloco opcional "state"). Campo ausente NUNCA
    /// zera o valor conhecido: o agente pode ser antigo ou omitir campos.
    /// </summary>
    private static void ApplyServiceState(
        AgentWindowsUpdateSnapshot snapshot,
        PatchServiceStateMeta? state,
        DateTime now)
    {
        if (state is null || state.IsEmpty)
            return;

        if (!string.IsNullOrWhiteSpace(state.ServiceState))
            snapshot.ServiceState = state.ServiceState;
        if (state.WuOnline.HasValue)
            snapshot.WuOnline = state.WuOnline.Value;
        if (state.PolicyManaged.HasValue)
            snapshot.PolicyManaged = state.PolicyManaged.Value;
        if (state.LastInstallSuccessAt.HasValue)
            snapshot.LastInstallSuccessAt = state.LastInstallSuccessAt.Value.ToUniversalTime();
        if (state.DownloadInProgress.HasValue)
            snapshot.DownloadInProgress = state.DownloadInProgress.Value;
        if (!string.IsNullOrWhiteSpace(state.StateHash))
            snapshot.StateHash = state.StateHash;

        snapshot.StateChangedAt = state.StateChangedAt?.ToUniversalTime() ?? now;
    }

    private async Task ApplyAgentPendingRebootAsync(
        Guid agentId,
        PatchPendingRebootMeta meta,
        DateTime now,
        CancellationToken ct)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(item => item.Id == agentId, ct);
        if (agent is null)
            return;

        agent.PendingReboot = meta.Pending;
        agent.PendingRebootReasons = PatchJson.SerializeStringArray(meta.Reasons?.ToArray());
        agent.PendingRebootCheckedAt = meta.CheckedAt?.ToUniversalTime() ?? now;
        agent.UpdatedAt = now;
    }

    public async Task<int> PurgeStaleInventoryAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var stale = await db.AgentWindowsUpdates
            .Where(update => !update.IsPresent && update.LastSeenAt < cutoff)
            .ToListAsync(ct);
        if (stale.Count == 0)
            return 0;

        db.AgentWindowsUpdates.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task<int> ExpireStaleInstallingAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var rows = await db.AgentWindowsUpdates
            .Where(update => update.Status == PatchUpdateStatus.Installing &&
                             update.IsPresent &&
                             (update.InstallStartedAt == null || update.InstallStartedAt < cutoff))
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.Status = PatchUpdateStatus.Failed;
            row.InstallError = "sem resultado do agente (timeout)";
            row.UpdatedAt = DateTime.UtcNow;
        }

        if (rows.Count > 0)
            await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task UpdateInstallStateAsync(
        Guid agentId,
        IReadOnlyCollection<string> updateKeys,
        PatchUpdateStatus status,
        int? resultCode,
        string? error,
        CancellationToken ct = default)
    {
        if (updateKeys.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var rows = await db.AgentWindowsUpdates
            .Where(update => update.AgentId == agentId && updateKeys.Contains(update.UpdateKey))
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.Status = status;
            row.InstallResultCode = resultCode;
            row.InstallError = error;
            row.UpdatedAt = now;

            if (status == PatchUpdateStatus.Installing)
                row.InstallStartedAt = now;

            // Instalação confirmada pelo agente: a linha deixa de ser pendente.
            // O próximo scan cria/atualiza a linha de "instalada" via QFE.
            if (status == PatchUpdateStatus.Installed)
            {
                row.IsPending = false;
                row.IsInstalled = true;
                row.InstalledAt ??= now;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ── Frota ───────────────────────────────────────────────────────────────

    private IQueryable<PatchRowContext> BuildScopedRows(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds)
    {
        var query =
            from update in db.AgentWindowsUpdates.AsNoTracking()
            join agent in db.Agents.AsNoTracking() on update.AgentId equals agent.Id
            join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where update.IsPresent && agent.DeletedAt == null
            select new PatchRowContext
            {
                Update = update,
                Agent = agent,
                Site = site,
                Client = client
            };

        if (clientId.HasValue)
            query = query.Where(row => row.Client.Id == clientId.Value);
        if (siteId.HasValue)
            query = query.Where(row => row.Site.Id == siteId.Value);

        if (!hasGlobalAccess)
        {
            var allowed = allowedClientIds.ToList();
            query = query.Where(row => allowed.Contains(row.Client.Id));
        }

        return query;
    }

    public async Task<IReadOnlyList<PatchInventoryGroup>> GetInventoryGroupsAsync(
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        string? search,
        string? severity,
        string? status,
        bool descending,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, MaxLimit);
        var query = BuildScopedRows(clientId, siteId, hasGlobalAccess, allowedClientIds);

        if (!string.IsNullOrWhiteSpace(severity))
        {
            var severityValue = severity.Trim();
            query = query.Where(row => row.Update.Severity != null &&
                EF.Functions.ILike(row.Update.Severity, severityValue));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = normalized switch
            {
                "pending" => query.Where(row => row.Update.IsPending),
                "installed" => query.Where(row => row.Update.IsInstalled),
                "denied" => query.Where(row => row.Update.ApprovalState == PatchApprovalState.Denied),
                "approved" => query.Where(row => row.Update.ApprovalState == PatchApprovalState.Approved ||
                                                  row.Update.ApprovalState == PatchApprovalState.AutoApproved),
                "failed" => query.Where(row => row.Update.Status == PatchUpdateStatus.Failed),
                _ => query
            };
        }

        var patterns = SoftwareSearchTerms.BuildPatterns(search);
        if (patterns is not null)
        {
            foreach (var pattern in patterns)
            {
                var term = pattern;
                query = query.Where(row =>
                    EF.Functions.ILike(row.Update.Title, term, SoftwareSearchTerms.EscapeCharacter) ||
                    EF.Functions.ILike(row.Update.GroupKey, term, SoftwareSearchTerms.EscapeCharacter) ||
                    (row.Update.Classification != null &&
                     EF.Functions.ILike(row.Update.Classification, term, SoftwareSearchTerms.EscapeCharacter)));
            }
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorKey = DecodeKeyCursor(cursor);
            if (!string.IsNullOrEmpty(cursorKey))
            {
                query = descending
                    ? query.Where(row => row.Update.GroupKey.CompareTo(cursorKey) < 0)
                    : query.Where(row => row.Update.GroupKey.CompareTo(cursorKey) > 0);
            }
        }

        var grouped = query
            .GroupBy(row => row.Update.GroupKey)
            .Select(group => new PatchInventoryGroup
            {
                GroupKey = group.Key,
                Title = group.Max(row => row.Update.Title),
                Classification = group.Max(row => row.Update.Classification),
                Severity = group.Max(row => row.Update.Severity),
                PublishedAt = group.Max(row => row.Update.PublishedAt),
                PendingAgents = group.Count(row => row.Update.IsPending),
                InstalledAgents = group.Count(row => row.Update.IsInstalled),
                DeniedAgents = group.Count(row => row.Update.ApprovalState == PatchApprovalState.Denied),
                ReportingAgents = group.Select(row => row.Update.AgentId).Distinct().Count(),
                FirstSeenAt = group.Min(row => row.Update.FirstSeenAt),
                LastSeenAt = group.Max(row => row.Update.LastSeenAt)
            });

        grouped = descending
            ? grouped.OrderByDescending(group => group.GroupKey)
            : grouped.OrderBy(group => group.GroupKey);

        return await grouped.Take(safeLimit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PatchInventoryAgentRow>> GetInventoryAgentsAsync(
        string groupKey,
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        PatchUpdateStatus? status,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, MaxLimit);
        var query = BuildScopedRows(clientId, siteId, hasGlobalAccess, allowedClientIds)
            .Where(row => row.Update.GroupKey == groupKey);

        if (status.HasValue)
            query = query.Where(row => row.Update.Status == status.Value);

        if (CursorPaginationHelper.TryDecodeGuidCursor(cursor, out var cursorAgentId))
            query = query.Where(row => row.Agent.Id.CompareTo(cursorAgentId) > 0);

        return await query
            .OrderBy(row => row.Agent.Id)
            .Take(safeLimit)
            .Select(row => new PatchInventoryAgentRow
            {
                AgentId = row.Agent.Id,
                UpdateKey = row.Update.UpdateKey,
                Hostname = row.Agent.Hostname,
                AgentDisplayName = row.Agent.DisplayName,
                SiteId = row.Site.Id,
                SiteName = row.Site.Name,
                ClientId = row.Client.Id,
                ClientName = row.Client.Name,
                Status = row.Update.Status,
                ApprovalState = row.Update.ApprovalState,
                Severity = row.Update.Severity,
                RequiresReboot = row.Update.RequiresReboot,
                ReportedAt = row.Update.ReportedAt,
                LastSeenAt = row.Update.LastSeenAt
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PatchPendingAgentRow>> GetPendingAgentsAsync(
        Guid? clientId,
        Guid? siteId,
        string? cursor,
        int limit,
        string? search,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, MaxLimit);
        var query =
            from update in db.AgentWindowsUpdates.AsNoTracking()
            join agent in db.Agents.AsNoTracking() on update.AgentId equals agent.Id
            join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in db.Clients.AsNoTracking() on site.ClientId equals client.Id
            join snapshot in db.AgentWindowsUpdateSnapshots.AsNoTracking() on update.AgentId equals snapshot.AgentId
            where update.IsPresent && update.IsPending && agent.DeletedAt == null
            select new { update, agent, site, client, snapshot };

        if (clientId.HasValue)
            query = query.Where(row => row.client.Id == clientId.Value);
        if (siteId.HasValue)
            query = query.Where(row => row.site.Id == siteId.Value);
        if (!hasGlobalAccess)
        {
            var allowed = allowedClientIds.ToList();
            query = query.Where(row => allowed.Contains(row.client.Id));
        }

        var patterns = SoftwareSearchTerms.BuildPatterns(search);
        if (patterns is not null)
        {
            foreach (var pattern in patterns)
            {
                var term = pattern;
                query = query.Where(row =>
                    EF.Functions.ILike(row.agent.Hostname, term, SoftwareSearchTerms.EscapeCharacter) ||
                    (row.agent.DisplayName != null &&
                     EF.Functions.ILike(row.agent.DisplayName, term, SoftwareSearchTerms.EscapeCharacter)) ||
                    EF.Functions.ILike(row.client.Name, term, SoftwareSearchTerms.EscapeCharacter) ||
                    EF.Functions.ILike(row.site.Name, term, SoftwareSearchTerms.EscapeCharacter));
            }
        }

        if (CursorPaginationHelper.TryDecodeGuidCursor(cursor, out var cursorAgentId))
            query = query.Where(row => row.agent.Id.CompareTo(cursorAgentId) > 0);

        return await query
            .GroupBy(row => new
            {
                row.agent.Id,
                row.agent.Hostname,
                row.agent.DisplayName,
                SiteId = row.site.Id,
                SiteName = row.site.Name,
                ClientId = row.client.Id,
                ClientName = row.client.Name,
                row.snapshot.ScanStatus,
                row.snapshot.CollectedAt,
                row.agent.PendingReboot,
                row.agent.LastSeenAt
            })
            .OrderBy(group => group.Key.Id)
            .Take(safeLimit)
            .Select(group => new PatchPendingAgentRow
            {
                AgentId = group.Key.Id,
                Hostname = group.Key.Hostname,
                AgentDisplayName = group.Key.DisplayName,
                SiteId = group.Key.SiteId,
                SiteName = group.Key.SiteName,
                ClientId = group.Key.ClientId,
                ClientName = group.Key.ClientName,
                // Pendentes "acionáveis" (recusadas ficam no contador próprio).
                PendingCount = group.Count(row => row.update.ApprovalState != PatchApprovalState.Denied),
                CriticalCount = group.Count(row => row.update.ApprovalState != PatchApprovalState.Denied &&
                    row.update.Severity != null &&
                    EF.Functions.ILike(row.update.Severity, "critical")),
                DeniedCount = group.Count(row => row.update.ApprovalState == PatchApprovalState.Denied),
                PendingReboot = group.Key.PendingReboot,
                ScanStatus = group.Key.ScanStatus,
                LastScanAt = group.Key.CollectedAt,
                LastSeenAt = group.Key.LastSeenAt
            })
            .ToListAsync(ct);
    }

    public async Task<PatchScopeSnapshot> GetScopeSnapshotAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var pendingQuery = BuildScopedRows(clientId, siteId, hasGlobalAccess, allowedClientIds)
            .Where(row => row.Update.IsPending);

        // Conformidade mede o que está PENDENTE E NÃO BLOQUEADO: um KB recusado
        // de propósito pelo operador não pode derrubar o percentual da frota
        // (decisão do produto). Recusados continuam em contador próprio/tile.
        var actionableQuery = pendingQuery
            .Where(row => row.Update.ApprovalState != PatchApprovalState.Denied);

        var snapshot = new PatchScopeSnapshot
        {
            TotalPending = await actionableQuery.CountAsync(ct),
            CriticalPending = await actionableQuery.CountAsync(row => row.Update.Severity != null &&
                EF.Functions.ILike(row.Update.Severity, "critical"), ct),
            DeniedPending = await pendingQuery.CountAsync(row =>
                row.Update.ApprovalState == PatchApprovalState.Denied, ct),
            ApprovedPending = await actionableQuery.CountAsync(row =>
                row.Update.ApprovalState == PatchApprovalState.Approved ||
                row.Update.ApprovalState == PatchApprovalState.AutoApproved, ct),
            AgentsWithPending = await actionableQuery.Select(row => row.Update.AgentId).Distinct().CountAsync(ct),
            FailedInstallations24h = await actionableQuery.CountAsync(row =>
                row.Update.Status == PatchUpdateStatus.Failed && row.Update.UpdatedAt >= now.AddHours(-24), ct)
        };

        var agentScope =
            from agent in db.Agents.AsNoTracking()
            join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where agent.DeletedAt == null
            select new { agent, site, client };

        if (clientId.HasValue)
            agentScope = agentScope.Where(row => row.client.Id == clientId.Value);
        if (siteId.HasValue)
            agentScope = agentScope.Where(row => row.site.Id == siteId.Value);
        if (!hasGlobalAccess)
        {
            var allowed = allowedClientIds.ToList();
            agentScope = agentScope.Where(row => allowed.Contains(row.client.Id));
        }

        snapshot.AgentsTotal = await agentScope.CountAsync(ct);
        snapshot.RebootPendingAgents = await agentScope.CountAsync(row => row.agent.PendingReboot, ct);

        var snapshotScope =
            from snap in db.AgentWindowsUpdateSnapshots.AsNoTracking()
            join agent in db.Agents.AsNoTracking() on snap.AgentId equals agent.Id
            join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where agent.DeletedAt == null
            select new { snap, agent, site, client };

        if (clientId.HasValue)
            snapshotScope = snapshotScope.Where(row => row.client.Id == clientId.Value);
        if (siteId.HasValue)
            snapshotScope = snapshotScope.Where(row => row.site.Id == siteId.Value);
        if (!hasGlobalAccess)
        {
            var allowed = allowedClientIds.ToList();
            snapshotScope = snapshotScope.Where(row => allowed.Contains(row.client.Id));
        }

        snapshot.AgentsReporting = await snapshotScope.CountAsync(ct);
        snapshot.LastScanAt = await snapshotScope
            .Select(row => row.snap.CollectedAt)
            .MaxAsync(ct);

        return snapshot;
    }

    public async Task<IReadOnlyList<PatchSeverityCount>> GetPendingBySeverityAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 50);
        return await BuildScopedRows(clientId, siteId, hasGlobalAccess, allowedClientIds)
            .Where(row => row.Update.IsPending && row.Update.ApprovalState != PatchApprovalState.Denied)
            .GroupBy(row => row.Update.Severity ?? "Unspecified")
            .Select(group => new PatchSeverityCount { Label = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PatchSeverityCount>> GetPendingByClassificationAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 50);
        return await BuildScopedRows(clientId, siteId, hasGlobalAccess, allowedClientIds)
            .Where(row => row.Update.IsPending && row.Update.ApprovalState != PatchApprovalState.Denied)
            .GroupBy(row => row.Update.Classification ?? "Unspecified")
            .Select(group => new PatchSeverityCount { Label = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAgentIdsInScopeAsync(
        Guid? clientId,
        Guid? siteId,
        bool hasGlobalAccess,
        IReadOnlyCollection<Guid> allowedClientIds,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);
        var query =
            from agent in db.Agents.AsNoTracking()
            join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
            join client in db.Clients.AsNoTracking() on site.ClientId equals client.Id
            where agent.DeletedAt == null && agent.Status == AgentStatus.Online
            select new { agent, site, client };

        if (clientId.HasValue)
            query = query.Where(row => row.client.Id == clientId.Value);
        if (siteId.HasValue)
            query = query.Where(row => row.site.Id == siteId.Value);
        if (!hasGlobalAccess)
        {
            var allowed = allowedClientIds.ToList();
            query = query.Where(row => allowed.Contains(row.client.Id));
        }

        return await query.Select(row => row.agent.Id).Take(safeLimit).ToListAsync(ct);
    }

    // ── Aprovações ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PatchApproval>> GetApprovalsAsync(
        PatchApprovalScopeType? scopeType,
        Guid? scopeId,
        string? search,
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, MaxLimit);
        var query = db.PatchApprovals.AsNoTracking().AsQueryable();

        if (scopeType.HasValue)
            query = query.Where(rule => rule.ScopeType == scopeType.Value);
        if (scopeId.HasValue)
            query = query.Where(rule => rule.ScopeId == scopeId.Value);

        var patterns = SoftwareSearchTerms.BuildPatterns(search);
        if (patterns is not null)
        {
            foreach (var pattern in patterns)
            {
                var term = pattern;
                query = query.Where(rule =>
                    EF.Functions.ILike(rule.UpdateKey, term, SoftwareSearchTerms.EscapeCharacter) ||
                    (rule.KbId != null && EF.Functions.ILike(rule.KbId, term, SoftwareSearchTerms.EscapeCharacter)) ||
                    (rule.Reason != null && EF.Functions.ILike(rule.Reason, term, SoftwareSearchTerms.EscapeCharacter)));
            }
        }

        return await query
            .OrderByDescending(rule => rule.UpdatedAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public Task<PatchApproval?> GetApprovalAsync(
        PatchApprovalScopeType scopeType,
        Guid? scopeId,
        string updateKey,
        CancellationToken ct = default)
        => db.PatchApprovals.FirstOrDefaultAsync(rule =>
            rule.ScopeType == scopeType &&
            rule.ScopeId == scopeId &&
            rule.UpdateKey == updateKey, ct);

    public Task<PatchApproval?> GetApprovalByIdAsync(Guid id, CancellationToken ct = default)
        => db.PatchApprovals.AsNoTracking().FirstOrDefaultAsync(rule => rule.Id == id, ct);

    public async Task AddApprovalAuditAsync(PatchApprovalAudit audit, CancellationToken ct = default)
    {
        db.PatchApprovalAudits.Add(audit);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PatchApproval> UpsertApprovalAsync(
        PatchApproval approval,
        PatchApprovalAudit audit,
        CancellationToken ct = default)
    {
        var existing = await db.PatchApprovals.FirstOrDefaultAsync(rule =>
            rule.ScopeType == approval.ScopeType &&
            rule.ScopeId == approval.ScopeId &&
            rule.UpdateKey == approval.UpdateKey, ct);

        if (existing is null)
        {
            db.PatchApprovals.Add(approval);
            db.PatchApprovalAudits.Add(audit);
            await db.SaveChangesAsync(ct);
            return approval;
        }

        existing.Action = approval.Action;
        existing.Reason = approval.Reason;
        existing.KbId = approval.KbId;
        existing.ClientId = approval.ClientId;
        existing.SiteId = approval.SiteId;
        existing.AgentId = approval.AgentId;
        existing.UpdatedBy = approval.UpdatedBy;
        existing.UpdatedAt = DateTime.UtcNow;

        audit.RuleId = existing.Id;
        db.PatchApprovalAudits.Add(audit);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteApprovalAsync(
        Guid id,
        string? changedBy,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var existing = await db.PatchApprovals.FirstOrDefaultAsync(rule => rule.Id == id, ct);
        if (existing is null)
            return false;

        db.PatchApprovalAudits.Add(new PatchApprovalAudit
        {
            Id = Guid.NewGuid(),
            RuleId = existing.Id,
            ChangeType = PatchApprovalAuditChangeType.Deleted,
            ScopeType = existing.ScopeType,
            ScopeId = existing.ScopeId,
            ClientId = existing.ClientId,
            SiteId = existing.SiteId,
            AgentId = existing.AgentId,
            UpdateKey = existing.UpdateKey,
            KbId = existing.KbId,
            OldAction = existing.Action,
            NewAction = null,
            Reason = existing.Reason,
            ChangedBy = changedBy,
            IpAddress = ipAddress,
            ChangedAt = DateTime.UtcNow
        });

        db.PatchApprovals.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<PatchApprovalAudit>> GetApprovalAuditAsync(
        int limit,
        CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, MaxLimit);
        return await db.PatchApprovalAudits
            .AsNoTracking()
            .OrderByDescending(audit => audit.ChangedAt)
            .Take(safeLimit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PatchApproval>> GetApplicableApprovalsForAgentAsync(
        Guid agentId,
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default)
        => await db.PatchApprovals
            .AsNoTracking()
            .Where(rule =>
                (rule.ScopeType == PatchApprovalScopeType.Global) ||
                (rule.ScopeType == PatchApprovalScopeType.Client && rule.ClientId == clientId) ||
                (rule.ScopeType == PatchApprovalScopeType.Site && rule.SiteId == siteId) ||
                (rule.ScopeType == PatchApprovalScopeType.Agent && rule.AgentId == agentId))
            .ToListAsync(ct);

    // ── Políticas ───────────────────────────────────────────────────────────

    public Task<PatchPolicy?> GetPolicyAsync(
        PatchApprovalScopeType scopeType,
        Guid? scopeId,
        CancellationToken ct = default)
        => db.PatchPolicies.AsNoTracking()
            .FirstOrDefaultAsync(policy => policy.ScopeType == scopeType && policy.ScopeId == scopeId, ct);

    public async Task<IReadOnlyList<PatchPolicy>> GetApplicablePoliciesForAgentAsync(
        Guid clientId,
        Guid siteId,
        CancellationToken ct = default)
        => await db.PatchPolicies
            .AsNoTracking()
            .Where(policy =>
                policy.ScopeType == PatchApprovalScopeType.Global ||
                (policy.ScopeType == PatchApprovalScopeType.Client && policy.ClientId == clientId) ||
                (policy.ScopeType == PatchApprovalScopeType.Site && policy.SiteId == siteId))
            .ToListAsync(ct);

    public async Task<PatchPolicy> UpsertPolicyAsync(PatchPolicy policy, CancellationToken ct = default)
    {
        var existing = await db.PatchPolicies.FirstOrDefaultAsync(item =>
            item.ScopeType == policy.ScopeType && item.ScopeId == policy.ScopeId, ct);

        if (existing is null)
        {
            db.PatchPolicies.Add(policy);
            await db.SaveChangesAsync(ct);
            return policy;
        }

        existing.EnforcementMode = policy.EnforcementMode;
        existing.AutoApproveClassifications = policy.AutoApproveClassifications;
        existing.AutoApproveSeverities = policy.AutoApproveSeverities;
        existing.AutoInstallEnabled = policy.AutoInstallEnabled;
        existing.MaintenanceWindowJson = policy.MaintenanceWindowJson;
        existing.RebootPolicy = policy.RebootPolicy;
        existing.RebootAllowed = policy.RebootAllowed;
        existing.MaxInstallsPerRun = policy.MaxInstallsPerRun;
        existing.HideDeniedUpdates = policy.HideDeniedUpdates;
        existing.UpdatedBy = policy.UpdatedBy;
        existing.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<IReadOnlyList<PatchPolicy>> GetPoliciesAsync(CancellationToken ct = default)
        => await db.PatchPolicies.AsNoTracking().OrderBy(policy => policy.ScopeType).ToListAsync(ct);

    // ── Enforcement no SO ───────────────────────────────────────────────────

    public async Task<PatchOsState> SaveOsStateAsync(PatchOsState state, CancellationToken ct = default)
    {
        db.PatchOsStates.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    public Task<PatchOsState?> GetLatestOsStateAsync(Guid agentId, CancellationToken ct = default)
        => db.PatchOsStates.AsNoTracking()
            .Where(state => state.AgentId == agentId && state.RevertedAt == null)
            .OrderByDescending(state => state.AppliedAt)
            .FirstOrDefaultAsync(ct);

    public async Task UpdateOsStateBackupAsync(Guid agentId, string previousJson, CancellationToken ct = default)
    {
        var state = await db.PatchOsStates
            .Where(item => item.AgentId == agentId && item.RevertedAt == null)
            .OrderByDescending(item => item.AppliedAt)
            .FirstOrDefaultAsync(ct);
        if (state is null)
            return;

        state.PreviousJson = previousJson;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkOsStateRevertedAsync(Guid stateId, string? revertedBy, CancellationToken ct = default)
    {
        var state = await db.PatchOsStates.FirstOrDefaultAsync(item => item.Id == stateId, ct);
        if (state is null)
            return;
        state.RevertedAt = DateTime.UtcNow;
        state.RevertedBy = revertedBy;
        await db.SaveChangesAsync(ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string BuildGroupKey(AgentWindowsUpdateEntry entry)
    {
        var kbIds = PatchJson.ParseStringArray(entry.KbIds);
        if (kbIds.Count > 0)
            return "kb:" + kbIds[0];
        return entry.UpdateKey;
    }

    private static string? DecodeKeyCursor(string cursor)
    {
        try
        {
            var raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }
        catch
        {
            return cursor;
        }
    }

    /// <summary>
    /// Projeção intermediária das consultas de frota. É uma CLASSE com
    /// inicializador de objeto (não record com construtor): o EF compõe filtros
    /// sobre projeções de inicializador de forma previsível — projeções por
    /// construtor em query intermediária correm risco de "could not be translated".
    /// </summary>
    private sealed class PatchRowContext
    {
        public AgentWindowsUpdate Update { get; init; } = null!;
        public Agent Agent { get; init; } = null!;
        public Site Site { get; init; } = null!;
        public Client Client { get; init; } = null!;
    }
}
