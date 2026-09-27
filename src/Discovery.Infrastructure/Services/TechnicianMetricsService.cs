using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Métricas históricas por atendente para a triagem por IA.
///
/// Estratégia: snapshot materializado (technician_metrics_snapshots) com TTL
/// curto; quando vencido/ausente, recalcula apenas para os usuários pedidos e
/// regrava. O recálculo é feito em memória a partir de um conjunto reduzido de
/// colunas (sem depender de tradução de aritmética de datas pelo provider).
/// </summary>
public class TechnicianMetricsService(DiscoveryDbContext db) : ITechnicianMetricsService
{
    /// <summary>Validade do snapshot antes de recalcular sob demanda.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);

    public const int DefaultWindowDays = 90;

    private const int RefreshBatchSize = 200;

    public async Task<TechnicianMetricsDto> GetMetricsAsync(Guid userId, CancellationToken ct = default)
    {
        var snapshot = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (snapshot is not null && DateTime.UtcNow - snapshot.ComputedAt <= FreshFor)
            return Map(snapshot);

        var computed = await ComputeAsync([userId], snapshot?.WindowDays ?? DefaultWindowDays, ct);
        if (computed.Count > 0)
        {
            await PersistAsync(computed.Values, ct);
            return computed[userId];
        }

        return Empty(userId, snapshot?.WindowDays ?? DefaultWindowDays);
    }

    public async Task<IReadOnlyList<TechnicianMetricsDto>> GetMetricsForUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var snapshots = await db.TechnicianMetricsSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.UserId))
            .ToListAsync(ct);

        var fresh = snapshots
            .Where(s => DateTime.UtcNow - s.ComputedAt <= FreshFor)
            .ToDictionary(s => s.UserId, Map);

        var missing = ids.Where(id => !fresh.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var computed = await ComputeAsync(missing, DefaultWindowDays, ct);

            // Usuários sem chamados também recebem snapshot zerado: mantém o
            // snapshot coerente com a ausência de histórico e evita recalcular
            // a cada leitura.
            var toPersist = computed.Values.ToList();
            foreach (var id in missing.Where(id => !computed.ContainsKey(id)))
            {
                var empty = Empty(id, DefaultWindowDays);
                toPersist.Add(empty);
                fresh[id] = empty;
            }

            if (toPersist.Count > 0)
                await PersistAsync(toPersist, ct);

            foreach (var (userId, dto) in computed)
                fresh[userId] = dto;
        }

        return ids.Select(id => fresh[id]).ToList();
    }

    public async Task<int> RefreshSnapshotsAsync(
        IReadOnlyCollection<Guid>? userIds = null,
        Guid? departmentId = null,
        CancellationToken ct = default)
    {
        var targets = await ResolveTargetsAsync(userIds, departmentId, ct);
        if (targets.Count == 0) return 0;

        var saved = 0;
        foreach (var batch in Chunk(targets, RefreshBatchSize))
        {
            var computed = await ComputeAsync(batch, DefaultWindowDays, ct);
            if (computed.Count == 0) continue;

            await PersistAsync(computed.Values, ct);
            saved += computed.Count;
        }

        return saved;
    }

    private async Task<List<Guid>> ResolveTargetsAsync(
        IReadOnlyCollection<Guid>? userIds, Guid? departmentId, CancellationToken ct)
    {
        if (userIds is { Count: > 0 })
            return userIds.Distinct().ToList();

        var query = db.DepartmentMembers.AsNoTracking()
            .Join(db.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, _) => m);

        if (departmentId.HasValue)
        {
            query = query.Where(m => m.DepartmentId == departmentId.Value && m.IsActive);
        }
        else
        {
            query = query.Where(m => m.IsActive);
        }

        var members = await query.Select(m => m.UserId).Distinct().ToListAsync(ct);

        var assignees = await db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.AssignedToUserId != null)
            .Select(t => t.AssignedToUserId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return members.Concat(assignees).Distinct().ToList();
    }

    private async Task<Dictionary<Guid, TechnicianMetricsDto>> ComputeAsync(
        IReadOnlyCollection<Guid> userIds, int windowDays, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];

        var since = DateTime.UtcNow.AddDays(-windowDays);

        var rows = await db.Tickets.AsNoTracking()
            .Where(t => t.DeletedAt == null && t.AssignedToUserId != null
                        && ids.Contains(t.AssignedToUserId!.Value)
                        && (t.CreatedAt >= since || t.ClosedAt == null))
            .Select(t => new TicketMetricRow(
                t.AssignedToUserId!.Value,
                t.CreatedAt,
                t.FirstRespondedAt,
                t.ClosedAt,
                t.SlaBreached,
                t.Rating,
                t.Category))
            .ToListAsync(ct);

        var reopenRows = await db.TicketActivityLogs.AsNoTracking()
            .Where(l => l.Type == TicketActivityType.Reopened && l.CreatedAt >= since)
            .Join(
                db.Tickets.AsNoTracking().Where(t => t.AssignedToUserId != null && ids.Contains(t.AssignedToUserId!.Value)),
                l => l.TicketId,
                t => t.Id,
                (_, t) => t.AssignedToUserId!.Value)
            .GroupBy(userId => userId)
            .Select(group => new { UserId = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        var reopenByUser = reopenRows.ToDictionary(r => r.UserId, r => r.Count);

        // Dificuldade média dos chamados que a triagem atribuiu ao atendente. Sem
        // isso a dimensão de dificuldade do score ficava permanentemente neutra.
        var difficultyRows = await db.TicketAssignmentDecisions.AsNoTracking()
            .Where(d => d.ChosenUserId != null && d.Applied
                        && ids.Contains(d.ChosenUserId!.Value) && d.CreatedAt >= since)
            .GroupBy(d => d.ChosenUserId!.Value)
            .Select(g => new { UserId = g.Key, Average = g.Average(d => (double)d.Difficulty) })
            .ToListAsync(ct);

        var difficultyByUser = difficultyRows.ToDictionary(r => r.UserId, r => r.Average);

        var result = new Dictionary<Guid, TechnicianMetricsDto>();
        foreach (var group in rows.GroupBy(r => r.UserId))
        {
            var list = group.ToList();

            // Tempos/taxas consideram apenas tickets criados dentro da janela;
            // a carga atual (openNow) considera todos os abertos, inclusive os
            // anteriores à janela.
            var windowList = list.Where(r => r.CreatedAt >= since).ToList();
            var resolved = windowList.Where(r => r.ClosedAt.HasValue).ToList();

            var frt = windowList
                .Where(r => r.FirstRespondedAt.HasValue && r.FirstRespondedAt!.Value >= r.CreatedAt)
                .Select(r => (r.FirstRespondedAt!.Value - r.CreatedAt).TotalMinutes)
                .ToList();

            var resolution = resolved
                .Where(r => r.ClosedAt!.Value >= r.CreatedAt)
                .Select(r => (r.ClosedAt!.Value - r.CreatedAt).TotalMinutes)
                .ToList();

            var ratings = windowList.Where(r => r.Rating.HasValue).Select(r => (double)r.Rating!.Value).ToList();

            var topCategories = windowList
                .Where(r => !string.IsNullOrWhiteSpace(r.Category))
                .GroupBy(r => r.Category!.Trim())
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => g.Key)
                .Take(5)
                .ToList();

            var resolvedCount = resolved.Count;
            var reopenRate = resolvedCount > 0
                ? Math.Clamp(reopenByUser.GetValueOrDefault(group.Key) / (double)resolvedCount, 0, 1)
                : 0;
            var openNow = list.Count(r => !r.ClosedAt.HasValue);

            result[group.Key] = new TechnicianMetricsDto(
                group.Key,
                windowDays,
                windowList.Count,
                resolvedCount,
                openNow,
                Average(frt),
                Average(resolution),
                Percentile(resolution, 0.90),
                windowList.Count > 0 ? Math.Clamp(windowList.Count(r => r.SlaBreached) / (double)windowList.Count, 0, 1) : 0,
                reopenRate,
                Average(ratings),
                ratings.Count,
                difficultyByUser.TryGetValue(group.Key, out var difficultyAverage) ? difficultyAverage : null,
                topCategories,
                [],
                DateTime.UtcNow);
        }

        return result;
    }

    private async Task PersistAsync(IEnumerable<TechnicianMetricsDto> metrics, CancellationToken ct)
    {
        var list = metrics.ToList();
        if (list.Count == 0) return;

        var ids = list.Select(m => m.UserId).ToList();
        var existing = await db.TechnicianMetricsSnapshots
            .Where(s => ids.Contains(s.UserId))
            .ToListAsync(ct);

        db.TechnicianMetricsSnapshots.RemoveRange(existing);
        db.TechnicianMetricsSnapshots.AddRange(list.Select(ToEntity));
        await db.SaveChangesAsync(ct);
    }

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

    private static TechnicianMetricsDto Empty(Guid userId, int windowDays) => new(
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

    private static double? Average(IReadOnlyCollection<double> values)
        => values.Count == 0 ? null : values.Average();

    private static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return null;
        var ordered = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(percentile * ordered.Count) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
    }

    private static IEnumerable<List<Guid>> Chunk(List<Guid> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

}

internal sealed record TicketMetricRow(
    Guid UserId,
    DateTime CreatedAt,
    DateTime? FirstRespondedAt,
    DateTime? ClosedAt,
    bool SlaBreached,
    int? Rating,
    string? Category);
