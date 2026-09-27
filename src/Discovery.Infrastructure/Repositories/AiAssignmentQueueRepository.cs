using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Discovery.Infrastructure.Repositories;

/// <summary>
/// Fila de triagem por IA. Segue o mesmo padrão da fila de embeddings da KB:
/// claim com FOR UPDATE SKIP LOCKED, retry com backoff e uma linha por chamado.
/// </summary>
public class AiAssignmentQueueRepository(DiscoveryDbContext db) : IAiAssignmentQueueRepository
{
    public async Task EnqueueAsync(Guid ticketId, Guid departmentId, string? reason, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO ai_assignment_queue
                (id, ticket_id, department_id, status, attempts, available_at, reason, created_at, updated_at)
            VALUES
                (@id, @ticket_id, @department_id, @status, 0, now(), @reason, now(), now())
            ON CONFLICT (ticket_id)
            DO UPDATE SET
                department_id = EXCLUDED.department_id,
                status = EXCLUDED.status,
                attempts = 0,
                available_at = now(),
                reason = EXCLUDED.reason,
                last_error = NULL,
                updated_at = now();";

        var parameters = new[]
        {
            new NpgsqlParameter("id", Guid.NewGuid()),
            new NpgsqlParameter("ticket_id", ticketId),
            new NpgsqlParameter("department_id", departmentId),
            new NpgsqlParameter("status", AiAssignmentQueueStatus.Pending),
            new NpgsqlParameter("reason", (object?)reason ?? DBNull.Value)
        };

        await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
    }

    public async Task<IReadOnlyList<AiAssignmentQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE ai_assignment_queue
            SET status = @processing,
                attempts = attempts + 1,
                updated_at = now()
            WHERE id IN (
                SELECT id
                FROM ai_assignment_queue
                WHERE status IN (@pending, @failed)
                  AND available_at <= now()
                ORDER BY updated_at ASC
                FOR UPDATE SKIP LOCKED
                LIMIT @limit
            )
            RETURNING id, ticket_id, department_id, status, attempts, available_at, last_error, reason, created_at, updated_at;";

        var parameters = new[]
        {
            new NpgsqlParameter("processing", AiAssignmentQueueStatus.Processing),
            new NpgsqlParameter("pending", AiAssignmentQueueStatus.Pending),
            new NpgsqlParameter("failed", AiAssignmentQueueStatus.Failed),
            new NpgsqlParameter("limit", Math.Clamp(limit, 1, 200))
        };

        return await db.AiAssignmentQueueItems
            .FromSqlRaw(sql, parameters)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task MarkDoneAsync(Guid id, CancellationToken ct = default)
        => await SetStatusAsync(id, AiAssignmentQueueStatus.Done, null, null, ct);

    public async Task MarkSkippedAsync(Guid id, string reason, CancellationToken ct = default)
        => await SetStatusAsync(id, AiAssignmentQueueStatus.Skipped, reason, null, ct);

    public async Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default)
        => await SetStatusAsync(id, AiAssignmentQueueStatus.Failed, errorMessage, retryDelay, ct);

    public async Task<int> CountOutstandingAsync(CancellationToken ct = default)
        => await db.AiAssignmentQueueItems.AsNoTracking()
            .CountAsync(item => item.Status == AiAssignmentQueueStatus.Pending
                                || item.Status == AiAssignmentQueueStatus.Processing, ct);

    private async Task SetStatusAsync(Guid id, string status, string? error, TimeSpan? retryDelay, CancellationToken ct)
    {
        if (retryDelay is null)
        {
            const string sql = @"
                UPDATE ai_assignment_queue
                SET status = @status,
                    last_error = @error,
                    updated_at = now()
                WHERE id = @id;";

            await db.Database.ExecuteSqlRawAsync(sql, new[]
            {
                new NpgsqlParameter("status", status),
                new NpgsqlParameter("error", (object?)Truncate(error) ?? DBNull.Value),
                new NpgsqlParameter("id", id)
            }, ct);

            return;
        }

        const string retrySql = @"
            UPDATE ai_assignment_queue
            SET status = @status,
                last_error = @error,
                available_at = now() + (@delay_seconds || ' seconds')::interval,
                updated_at = now()
            WHERE id = @id;";

        await db.Database.ExecuteSqlRawAsync(retrySql, new[]
        {
            new NpgsqlParameter("status", status),
            new NpgsqlParameter("error", (object?)Truncate(error) ?? DBNull.Value),
            new NpgsqlParameter("delay_seconds", (int)Math.Max(1, retryDelay.Value.TotalSeconds)),
            new NpgsqlParameter("id", id)
        }, ct);
    }

    private static string? Truncate(string? value)
        => value is null ? null : (value.Length > 2000 ? value[..2000] : value);
}
