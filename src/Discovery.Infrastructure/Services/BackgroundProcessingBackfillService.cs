using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Backfill de snapshots de métricas: recálculo forçado da janela configurada.
///
/// Estado persistido em processing_scope_state (scope_type =
/// technician_metrics_backfill), o que dá progresso visível, retomada após
/// restart e execução única por escopo (pedido idempotente).
///
/// A sessão (SessionStartUtc) funciona como cursor: o refresh forçado recalcula
/// quem foi calculado ANTES do início da sessão, então cada lote avança sem
/// precisar guardar a lista de usuários.
/// </summary>
public class BackgroundProcessingBackfillService(
    DiscoveryDbContext db,
    ITechnicianMetricsService metricsService,
    IConfigurationResolver configurationResolver,
    ILogger<BackgroundProcessingBackfillService> logger) : IBackgroundProcessingBackfillService
{
    public const string BackfillScopeType = "technician_metrics_backfill";

    public const string StatusPending = "pending";
    public const string StatusRunning = "running";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";
    public const string StatusCancelled = "cancelled";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<BackgroundBackfillStateDto> RequestAsync(
        Guid? clientId, bool purgeOrphans, string? requestedBy, CancellationToken ct = default)
    {
        var scopeId = clientId ?? Guid.Empty;
        var row = await FindRowAsync(scopeId, ct);

        if (row is not null)
        {
            var existing = DeserializePayload(row);
            if (existing is not null
                && existing.Status is StatusPending or StatusRunning)
            {
                // Pedido idempotente: já existe trabalho para o escopo.
                logger.LogInformation("Backfill de métricas já está {Status} para o escopo {Scope}; pedido ignorado.", existing.Status, scopeId);
                return ToDto(scopeId, existing);
            }
        }

        var payload = new BackfillPayload(
            StatusPending,
            DateTime.UtcNow,
            requestedBy,
            null,
            0,
            0,
            purgeOrphans,
            null,
            null);

        await SaveAsync(row, scopeId, payload, ct);
        logger.LogInformation("Backfill de métricas pedido para o escopo {Scope} (purgeOrphans={Purge}).", scopeId, purgeOrphans);

        return ToDto(scopeId, payload);
    }

    public async Task<bool> CancelAsync(
    Guid? clientId, string? requestedBy, CancellationToken ct = default)
    {
        var scopeId = clientId ?? Guid.Empty;
        var row = await FindRowAsync(scopeId, ct);
        if (row is null) return false;

        var payload = DeserializePayload(row);
        if (payload is null || payload.Status is not (StatusPending or StatusRunning)) return false;

        await SaveAsync(row, scopeId, payload with
        {
            Status = StatusCancelled,
            LastError = requestedBy is null ? null : "cancelado por " + requestedBy
        }, ct);

        logger.LogInformation("Backfill de métricas cancelado para o escopo {Scope}.", scopeId);
        return true;
    }

    public async Task<BackgroundBackfillStateDto?> GetStateAsync(
        Guid? clientId, CancellationToken ct = default)
    {
        var scopeId = clientId ?? Guid.Empty;
        var row = await FindRowAsync(scopeId, ct);
        if (row is null) return null;

        var payload = DeserializePayload(row);
        return payload is null ? null : ToDto(scopeId, payload);
    }

    public async Task<BackgroundBackfillStateDto?> ProcessNextBatchAsync(CancellationToken ct = default)
    {
        var row = await db.ProcessingScopeStates
            .Where(state => state.ScopeType == BackfillScopeType)
            .OrderBy(state => state.UpdatedAt)
            .ToListAsync(ct);

        var next = row
            .Select(state => new { State = state, Payload = DeserializePayload(state) })
            .FirstOrDefault(item => item.Payload is not null
                && item.Payload.Status is StatusPending or StatusRunning);

        if (next?.Payload is null) return null;

        var scopeId = next.State.ScopeId;
        var payload = next.Payload;

        try
        {
            var sessionStart = payload.SessionStartUtc ?? DateTime.UtcNow;
            var settings = (await configurationResolver.ResolveBackgroundProcessingAsync(
                scopeId == Guid.Empty ? null : scopeId, ct)).Metrics;

            var maxUsers = Math.Max(1, settings.BatchSize * settings.MaxBatchesPerRun);

            var progress = await metricsService.RefreshForcedAsync(
                scopeId == Guid.Empty ? null : scopeId, sessionStart, maxUsers, ct);

            var completed = !progress.HasMore;

            if (completed && payload.PurgeOrphans && scopeId == Guid.Empty)
            {
                var removed = await metricsService.PurgeOrphanSnapshotsAsync(ct);
                logger.LogInformation("Backfill de métricas: {Count} snapshot(s) órfão(s) removido(s).", removed);
            }

            var updated = payload with
            {
                Status = completed ? StatusCompleted : StatusRunning,
                SessionStartUtc = sessionStart,
                Total = progress.Total,
                Processed = progress.Processed,
                CompletedAt = completed ? DateTime.UtcNow : null
            };

            await SaveAsync(next.State, scopeId, updated, ct);

            if (completed)
                logger.LogInformation("Backfill de métricas concluído para o escopo {Scope}: {Processed}/{Total}.", scopeId, progress.Processed, progress.Total);

            return ToDto(scopeId, updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backfill de métricas falhou para o escopo {Scope}.", scopeId);

            var failed = payload with { Status = StatusFailed, LastError = ex.Message };
            await SaveAsync(next.State, scopeId, failed, ct);
            return ToDto(scopeId, failed);
        }
    }

    private Task<ProcessingScopeState?> FindRowAsync(Guid scopeId, CancellationToken ct)
        => db.ProcessingScopeStates.FirstOrDefaultAsync(
            state => state.ScopeType == BackfillScopeType && state.ScopeId == scopeId, ct);

    private async Task SaveAsync(
        ProcessingScopeState? row, Guid scopeId, BackfillPayload payload, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(payload);

        if (row is null)
        {
            db.ProcessingScopeStates.Add(new ProcessingScopeState
            {
                Id = Guid.NewGuid(),
                ScopeType = BackfillScopeType,
                ScopeId = scopeId,
                LastRunAt = now,
                LastResultJson = json,
                UpdatedAt = now
            });
        }
        else
        {
            row.LastRunAt = now;
            row.LastResultJson = json;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private static BackfillPayload? DeserializePayload(ProcessingScopeState row)
    {
        if (string.IsNullOrWhiteSpace(row.LastResultJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<BackfillPayload>(row.LastResultJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BackgroundBackfillStateDto ToDto(Guid scopeId, BackfillPayload payload)
        => new(
            scopeId,
            payload.Status,
            payload.RequestedAt,
            payload.RequestedBy,
            payload.Total,
            payload.Processed,
            payload.PurgeOrphans,
            payload.LastError,
            payload.CompletedAt);

    private sealed record BackfillPayload(
        string Status,
        DateTime RequestedAt,
        string? RequestedBy,
        DateTime? SessionStartUtc,
        int Total,
        int Processed,
        bool PurgeOrphans,
        string? LastError,
        DateTime? CompletedAt);
}
