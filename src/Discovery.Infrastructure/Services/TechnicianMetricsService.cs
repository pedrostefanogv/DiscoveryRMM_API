using System.Diagnostics;
using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Métricas históricas por atendente para a triagem por IA.
///
/// Leitura: usa SEMPRE o snapshot materializado. Snapshot vencido não é
/// recalculado dentro de requisição — a atualização é responsabilidade do ciclo
/// agendado (RefreshDueAsync). A única exceção é o bootstrap de quem nunca teve
/// snapshot (configurável).
///
/// Ciclo: percorre os escopos de cliente vencidos (IntervalMinutes) e atualiza
/// até BatchSize usuários por lote, com teto de lotes e de tempo por execução.
///
/// Cálculo: agregação em SQL (ITechnicianMetricsAggregationRepository) — uma
/// linha por atendente em vez de todos os tickets do lote em memória.
/// </summary>
public class TechnicianMetricsService(
    DiscoveryDbContext db,
    ITechnicianMetricsAggregationRepository aggregation,
    IConfigurationResolver configurationResolver) : ITechnicianMetricsService
{
    public const int DefaultWindowDays = 90;

    // ── Leitura ──────────────────────────────────────────────────────────

    public async Task<TechnicianMetricsDto> GetMetricsAsync(
        Guid userId, Guid? clientScope = null, CancellationToken ct = default)
    {
        var metricsSettings = await ResolveMetricsSettingsAsync(clientScope, ct);

        var snapshot = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (snapshot is not null)
            return Map(snapshot);

        if (!metricsSettings.BootstrapMissingSnapshots)
            return Empty(userId, metricsSettings.WindowDays);

        var computed = await ComputeAsync([userId], metricsSettings.WindowDays, ct);
        var dto = computed.TryGetValue(userId, out var found)
            ? found
            : Zeroed(userId, metricsSettings.WindowDays);

        await PersistAsync([dto], ct);
        return dto;
    }

    public async Task<IReadOnlyList<TechnicianMetricsDto>> GetMetricsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, Guid? clientScope = null, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var metricsSettings = await ResolveMetricsSettingsAsync(clientScope, ct);

        var snapshots = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.UserId))
            .ToListAsync(ct);

        var result = snapshots.ToDictionary(s => s.UserId, Map);
        var missing = ids.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count == 0)
            return ids.Select(id => result[id]).ToList();

        if (!metricsSettings.BootstrapMissingSnapshots)
        {
            foreach (var id in missing)
                result[id] = Empty(id, metricsSettings.WindowDays);

            return ids.Select(id => result[id]).ToList();
        }

        // Bootstrap: quem nunca teve snapshot é calculado uma única vez.
        var computed = await ComputeAsync(missing, metricsSettings.WindowDays, ct);
        var toPersist = new List<TechnicianMetricsDto>();
        foreach (var id in missing)
        {
            var dto = computed.TryGetValue(id, out var found)
                ? found
                : Zeroed(id, metricsSettings.WindowDays);
            toPersist.Add(dto);
            result[id] = dto;
        }

        await PersistAsync(toPersist, ct);
        return ids.Select(id => result[id]).ToList();
    }

    // ── Ação administrativa / refresh explícito ──────────────────────────

    public async Task<int> RefreshSnapshotsAsync(
        IReadOnlyCollection<Guid>? userIds = null,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        Guid? clientId = null;
        if (departmentId.HasValue)
        {
            clientId = await db.Departments.AsNoTracking()
                .Where(d => d.Id == departmentId.Value)
                .Select(d => d.ClientId)
                .FirstOrDefaultAsync(ct);
        }

        var metricsSettings = await ResolveMetricsSettingsAsync(clientId, ct);
        var targets = await ResolveTargetsAsync(userIds, departmentId, ct);
        if (targets.Count == 0) return 0;

        var saved = 0;
        foreach (var batch in Chunk(targets, metricsSettings.BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            var computed = await ComputeAsync(batch, metricsSettings.WindowDays, ct);
            var toPersist = batch
                .Select(id => computed.TryGetValue(id, out var dto) ? dto : Zeroed(id, metricsSettings.WindowDays))
                .ToList();

            await PersistAsync(toPersist, ct);
            saved += toPersist.Count;
        }

        return saved;
    }

    // ── Ciclo periódico ──────────────────────────────────────────────────

    public async Task<MetricsRefreshResult> RefreshDueAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var now = DateTime.UtcNow;

        var scopes = await LoadScopeTargetsAsync(ct);
        if (scopes.Count == 0)
            return new MetricsRefreshResult(0, 0, 0, new Dictionary<Guid, int>(), 0);

        var states = await db.ProcessingScopeStates
            .Where(s => s.ScopeType == ProcessingScopeTypes.TechnicianMetrics)
            .ToListAsync(ct);
        var stateByScope = states.ToDictionary(s => s.ScopeId);

        var settingsCache = new Dictionary<Guid, TechnicianMetricsProcessingSettings>();
        var updatedByClient = new Dictionary<Guid, int>();
        var scopesProcessed = 0;
        var usersUpdated = 0;
        var usersPending = 0;

        foreach (var (scopeId, userIds) in scopes.OrderBy(kv => kv.Key))
        {
            ct.ThrowIfCancellationRequested();

            if (!settingsCache.TryGetValue(scopeId, out var metricsSettings))
            {
                metricsSettings = await ResolveMetricsSettingsAsync(NormalizeScope(scopeId), ct);
                settingsCache[scopeId] = metricsSettings;
            }

            if (!metricsSettings.Enabled) continue;

            // Vencimento por escopo: o tick do job é a granularidade mínima.
            if (stateByScope.TryGetValue(scopeId, out var state)
                && now - state.LastRunAt < TimeSpan.FromMinutes(metricsSettings.IntervalMinutes))
            {
                continue;
            }

            // Carrega os snapshots do escopo só depois do vencimento: menos dados por
            // tick e um usuário compartilhado entre dois clientes não é recalculado
            // duas vezes no mesmo ciclo (o segundo escopo já vê o snapshot fresco).
            var snapshotTimes = await LoadSnapshotTimesAsync(userIds, ct);

            var cutoff = now.AddMinutes(-metricsSettings.StaleThresholdMinutes);
            var due = userIds
                .Where(id => !snapshotTimes.TryGetValue(id, out var computedAt) || computedAt < cutoff)
                .OrderBy(id => snapshotTimes.TryGetValue(id, out var computedAt) ? computedAt : DateTime.MinValue)
                .ThenBy(id => id)
                .ToList();

            if (due.Count == 0)
            {
                scopesProcessed++;
                await SaveScopeStateAsync(stateByScope, scopeId, 0, 0, ct);
                continue;
            }

            var deadline = DateTime.UtcNow.AddSeconds(metricsSettings.MaxRunSeconds);
            var scopeUpdated = 0;
            var batches = 0;

            for (var offset = 0;
                 offset < due.Count && batches < metricsSettings.MaxBatchesPerRun;
                 offset += metricsSettings.BatchSize, batches++)
            {
                if (DateTime.UtcNow >= deadline) break;

                var batch = due.Skip(offset).Take(metricsSettings.BatchSize).ToList();
                var computed = await ComputeAsync(batch, metricsSettings.WindowDays, ct);

                var toPersist = new List<TechnicianMetricsDto>();
                foreach (var id in batch)
                {
                    var dto = computed.TryGetValue(id, out var found)
                        ? found
                        : Zeroed(id, metricsSettings.WindowDays);

                    toPersist.Add(dto);
                    snapshotTimes[id] = dto.ComputedAt ?? DateTime.UtcNow;
                }

                await PersistAsync(toPersist, ct);
                scopeUpdated += toPersist.Count;
            }

            var scopePending = Math.Max(0, due.Count - scopeUpdated);
            usersUpdated += scopeUpdated;
            usersPending += scopePending;
            updatedByClient[scopeId] = scopeUpdated;
            scopesProcessed++;

            await SaveScopeStateAsync(stateByScope, scopeId, scopeUpdated, scopePending, ct);
        }

        stopwatch.Stop();
        return new MetricsRefreshResult(
            scopesProcessed, usersUpdated, usersPending, updatedByClient, stopwatch.ElapsedMilliseconds);
    }

    // ── Backfill (recálculo forçado) ─────────────────────────────────────

    public async Task<MetricsBackfillProgress> RefreshForcedAsync(
        Guid? clientId, DateTime sessionStartUtc, int maxUsers, CancellationToken ct = default)
    {
        // clientId null = todos os escopos (backfill global); caso contrário filtra
        // os alvos no banco (departamentos/chamados do escopo) — evita varrer todos
        // os departamentos e todos os responsáveis a cada lote.
        var scopes = await LoadScopeTargetsAsync(ct, clientId);

        // Guid.Empty = escopo global de departamentos sem cliente.
        var targetUsers = clientId is null
            ? scopes.Values.SelectMany(ids => ids).Distinct().ToList()
            : scopes.TryGetValue(clientId.Value, out var scopeUsers)
                ? scopeUsers.Distinct().ToList()
                : [];

        if (targetUsers.Count == 0)
            return new MetricsBackfillProgress(0, 0, false);

        var metricsSettings = await ResolveMetricsSettingsAsync(NormalizeScope(clientId), ct);
        var snapshotTimes = await LoadSnapshotTimesAsync(targetUsers, ct);

        // "Pendente" = nunca calculado OU calculado antes do início desta sessão.
        var pending = targetUsers
            .Where(id => !snapshotTimes.TryGetValue(id, out var computedAt) || computedAt < sessionStartUtc)
            .OrderBy(id => snapshotTimes.TryGetValue(id, out var computedAt) ? computedAt : DateTime.MinValue)
            .ThenBy(id => id)
            .ToList();

        if (pending.Count == 0)
            return new MetricsBackfillProgress(targetUsers.Count, targetUsers.Count, false);

        var batch = pending.Take(Math.Max(1, maxUsers)).ToList();
        var computed = await ComputeAsync(batch, metricsSettings.WindowDays, ct);

        var toPersist = batch
            .Select(id => computed.TryGetValue(id, out var dto) ? dto : Zeroed(id, metricsSettings.WindowDays))
            .ToList();

        await PersistAsync(toPersist, ct);

        var remaining = pending.Count - batch.Count;
        return new MetricsBackfillProgress(
            targetUsers.Count - remaining,
            targetUsers.Count,
            remaining > 0);
    }

    public async Task<int> PurgeOrphanSnapshotsAsync(CancellationToken ct = default)
    {
        var scopes = await LoadScopeTargetsAsync(ct);
        var valid = scopes.Values.SelectMany(ids => ids).Distinct().ToHashSet();

        // Primeiro só a coluna (barato) para descobrir os órfãos; depois carrega e
        // remove APENAS as linhas órfãs (antes: tabela inteira em memória).
        var userIds = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .Select(snapshot => snapshot.UserId)
            .ToListAsync(ct);

        var orphans = userIds.Where(id => !valid.Contains(id)).Distinct().ToList();
        if (orphans.Count == 0) return 0;

        var rows = await db.TechnicianMetricsSnapshots
            .Where(snapshot => orphans.Contains(snapshot.UserId))
            .ToListAsync(ct);

        db.TechnicianMetricsSnapshots.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    // ── Cálculo e persistência ───────────────────────────────────────────

    private async Task<Dictionary<Guid, TechnicianMetricsDto>> ComputeAsync(
        IReadOnlyCollection<Guid> userIds, int windowDays, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var since = DateTime.UtcNow.AddDays(-windowDays);

        var aggregates = await aggregation.GetAggregatesAsync(ids, since, ct);
        var categories = await aggregation.GetTopCategoriesAsync(ids, since, 5, ct);
        var reopens = await aggregation.GetReopenCountsAsync(ids, since, ct);
        var difficulties = await aggregation.GetDifficultyAveragesAsync(ids, since, ct);

        var categoriesByUser = categories
            .GroupBy(c => c.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.Category).ToList());

        var result = new Dictionary<Guid, TechnicianMetricsDto>();
        foreach (var row in aggregates)
        {
            var reopenRate = row.ResolvedTotal > 0
                ? Math.Clamp(reopens.GetValueOrDefault(row.UserId) / (double)row.ResolvedTotal, 0, 1)
                : 0;
            var slaBreachRate = row.AssignedTotal > 0
                ? Math.Clamp(row.SlaBreachedInWindow / (double)row.AssignedTotal, 0, 1)
                : 0;

            result[row.UserId] = new TechnicianMetricsDto(
                row.UserId,
                windowDays,
                row.AssignedTotal,
                row.ResolvedTotal,
                row.OpenNow,
                row.AvgFirstResponseMinutes,
                row.AvgResolutionMinutes,
                row.P90ResolutionMinutes,
                slaBreachRate,
                reopenRate,
                row.CsatAverage,
                row.CsatRatedCount,
                difficulties.TryGetValue(row.UserId, out var difficulty) ? difficulty : null,
                categoriesByUser.TryGetValue(row.UserId, out var cats) ? cats : [],
                [],
                DateTime.UtcNow);
        }

        return result;
    }

    private async Task PersistAsync(IReadOnlyCollection<TechnicianMetricsDto> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0) return;

        var ids = metrics.Select(m => m.UserId).ToList();
        var existing = await db.TechnicianMetricsSnapshots
            .Where(s => ids.Contains(s.UserId))
            .ToListAsync(ct);

        db.TechnicianMetricsSnapshots.RemoveRange(existing);
        db.TechnicianMetricsSnapshots.AddRange(metrics.Select(ToEntity));
        await db.SaveChangesAsync(ct);
    }

    // ── Alvos e estado ───────────────────────────────────────────────────

    /// <summary>
    /// Alvos do ciclo agrupados por cliente. Um usuário pode atender clientes
    /// diferentes; ele é processado no escopo do cliente com necessidade (dedupe
    /// por escopo). Departamento global entra no escopo Guid.Empty.
    /// </summary>
    /// <param name="onlyScope">
    /// Quando informado, filtra no banco os departamentos/chamados do escopo
    /// (Guid.Empty = departamentos sem cliente) em vez de varrer todos.
    /// </param>
    /// <summary>Alvos de um único escopo (departamentos do cliente + responsáveis por chamados dele).</summary>
    private async Task<Dictionary<Guid, List<Guid>>> LoadSingleScopeTargetsAsync(
        Guid scopeId, CancellationToken ct)
    {
        var users = new HashSet<Guid>();

        if (scopeId == Guid.Empty)
        {
            // Escopo global: departamentos sem cliente. Chamados sempre têm cliente.
            var globalMembers = await db.DepartmentMembers.AsNoTracking()
                .Where(m => m.IsActive)
                .Join(db.Departments.AsNoTracking().Where(d => d.ClientId == null),
                    m => m.DepartmentId, d => d.Id, (m, _) => m.UserId)
                .ToListAsync(ct);

            foreach (var userId in globalMembers)
                users.Add(userId);
        }
        else
        {
            var members = await db.DepartmentMembers.AsNoTracking()
                .Where(m => m.IsActive)
                .Join(db.Departments.AsNoTracking().Where(d => d.ClientId == scopeId),
                    m => m.DepartmentId, d => d.Id, (m, _) => m.UserId)
                .ToListAsync(ct);

            foreach (var userId in members)
                users.Add(userId);

            var assignees = await db.Tickets.AsNoTracking()
                .Where(t => t.DeletedAt == null && t.AssignedToUserId != null && t.ClientId == scopeId)
                .Select(t => t.AssignedToUserId!.Value)
                .Distinct()
                .ToListAsync(ct);

            foreach (var userId in assignees)
                users.Add(userId);
        }

        return users.Count == 0
            ? []
            : new Dictionary<Guid, List<Guid>> { [scopeId] = users.ToList() };
    }

    private async Task<Dictionary<Guid, List<Guid>>> LoadScopeTargetsAsync(
        CancellationToken ct, Guid? onlyScope = null)
    {
        if (onlyScope is { } scoped)
            return await LoadSingleScopeTargetsAsync(scoped, ct);

        var scopes = new Dictionary<Guid, HashSet<Guid>>();

        void Add(Guid? clientId, Guid userId)
        {
            var scopeId = clientId ?? Guid.Empty;
            if (!scopes.TryGetValue(scopeId, out var set))
            {
                set = [];
                scopes[scopeId] = set;
            }
            set.Add(userId);
        }

        var memberRows = await db.DepartmentMembers.AsNoTracking()
            .Where(m => m.IsActive)
            .Join(db.Departments.AsNoTracking(), m => m.DepartmentId, d => d.Id,
                (m, d) => new { d.ClientId, m.UserId })
            .ToListAsync(ct);

        foreach (var row in memberRows)
            Add(row.ClientId, row.UserId);

        var assigneeRows = await db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.AssignedToUserId != null)
            .Select(t => new { ClientId = (Guid?)t.ClientId, UserId = t.AssignedToUserId!.Value })
            .Distinct()
            .ToListAsync(ct);

        foreach (var row in assigneeRows)
            Add(row.ClientId, row.UserId);

        return scopes.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
    }

    private async Task<Dictionary<Guid, DateTime>> LoadSnapshotTimesAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];

        var rows = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId))
            .Select(s => new { s.UserId, s.ComputedAt })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.UserId, r => r.ComputedAt);
    }

    private async Task SaveScopeStateAsync(
        Dictionary<Guid, ProcessingScopeState> stateByScope,
        Guid scopeId, int updated, int pending, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var payload = JsonSerializer.Serialize(new { updated, pending, type = ProcessingScopeTypes.TechnicianMetrics });

        if (stateByScope.TryGetValue(scopeId, out var state))
        {
            state.LastRunAt = now;
            state.LastResultJson = payload;
            state.UpdatedAt = now;
        }
        else
        {
            state = new ProcessingScopeState
            {
                Id = Guid.NewGuid(),
                ScopeType = ProcessingScopeTypes.TechnicianMetrics,
                ScopeId = scopeId,
                LastRunAt = now,
                LastResultJson = payload,
                UpdatedAt = now
            };
            db.ProcessingScopeStates.Add(state);
            stateByScope[scopeId] = state;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Guid>> ResolveTargetsAsync(
        IReadOnlyCollection<Guid>? userIds, Guid? departmentId, CancellationToken ct)
    {
        if (userIds is { Count: > 0 })
            return userIds.Distinct().ToList();

        var query = db.DepartmentMembers.AsNoTracking()
            .Join(db.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, _) => m);

        query = departmentId.HasValue
            ? query.Where(m => m.DepartmentId == departmentId.Value && m.IsActive)
            : query.Where(m => m.IsActive);

        var members = await query.Select(m => m.UserId).Distinct().ToListAsync(ct);

        // Refresh por departamento: recalculamos APENAS os membros do departamento,
        // que são exatamente os alvos exibidos/usados na triagem dele. Antes, todos os
        // responsáveis do banco eram incluídos, transformando a ação em um recálculo
        // global (varredura da tabela de tickets inteira + N lotes) que estourava o
        // timeout de 60s da requisição e retornava erro na tela.
        if (departmentId.HasValue)
            return members;

        var assignees = await db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.AssignedToUserId != null)
            .Select(t => t.AssignedToUserId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return members.Concat(assignees).Distinct().ToList();
    }

    private async Task<TechnicianMetricsProcessingSettings> ResolveMetricsSettingsAsync(
        Guid? clientScope, CancellationToken ct)
    {
        var settings = await configurationResolver.ResolveBackgroundProcessingAsync(
            NormalizeScope(clientScope), ct);
        return settings.Metrics;
    }

    private static Guid? NormalizeScope(Guid? clientScope)
        => clientScope is null || clientScope.Value == Guid.Empty ? null : clientScope;

    private static Guid? NormalizeScope(Guid clientScope)
        => clientScope == Guid.Empty ? null : clientScope;

    // ── Mapeamento ───────────────────────────────────────────────────────

    private static TechnicianMetricsSnapshot ToEntity(TechnicianMetricsDto dto) => new()
    {
        Id = Guid.NewGuid(),
        UserId = dto.UserId,
        WindowDays = dto.WindowDays,
        ComputedAt = dto.ComputedAt ?? DateTime.UtcNow,
        AssignedTotal = dto.AssignedTotal,
        ResolvedTotal = dto.ResolvedTotal,
        OpenNow = dto.OpenNow,
        AvgFirstResponseMinutes = dto.AvgFirstResponseMinutes,
        AvgResolutionMinutes = dto.AvgResolutionMinutes,
        P90ResolutionMinutes = dto.P90ResolutionMinutes,
        SlaBreachRate = dto.SlaBreachRate,
        ReopenRate = dto.ReopenRate,
        CsatAverage = dto.CsatAverage,
        CsatRatedCount = dto.CsatRatedCount,
        DifficultyAverage = dto.DifficultyAverage,
        TopCategoriesJson = JsonSerializer.Serialize(dto.TopCategories),
        TopTagsJson = JsonSerializer.Serialize(dto.TopTags)
    };

    private static TechnicianMetricsDto Map(TechnicianMetricsSnapshot snapshot) => new(
        snapshot.UserId,
        snapshot.WindowDays,
        snapshot.AssignedTotal,
        snapshot.ResolvedTotal,
        snapshot.OpenNow,
        snapshot.AvgFirstResponseMinutes,
        snapshot.AvgResolutionMinutes,
        snapshot.P90ResolutionMinutes,
        snapshot.SlaBreachRate,
        snapshot.ReopenRate,
        snapshot.CsatAverage,
        snapshot.CsatRatedCount,
        snapshot.DifficultyAverage,
        Deserialize(snapshot.TopCategoriesJson),
        Deserialize(snapshot.TopTagsJson),
        snapshot.ComputedAt);

    /// <summary>Sem snapshot algum (ComputedAt null = "nunca calculado").</summary>
    private static TechnicianMetricsDto Empty(Guid userId, int windowDays) => new(
        userId, windowDays, 0, 0, 0, null, null, null, 0, 0, null, 0, null, [], [], null);

    /// <summary>Snapshot zerado para quem não tem histórico (evita recalcular sempre).</summary>
    private static TechnicianMetricsDto Zeroed(Guid userId, int windowDays) => new(
        userId, windowDays, 0, 0, 0, null, null, null, 0, 0, null, 0, null, [], [], DateTime.UtcNow);

    private static IReadOnlyList<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<List<Guid>> Chunk(List<Guid> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}
