using System;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Discovery.Infrastructure.Repositories;

public class KnowledgeEmbeddingQueueRepository(DiscoveryDbContext db) : IKnowledgeEmbeddingQueueRepository
{
    public const string NotificationChannel = "knowledge_embedding_queue";

    public async Task EnqueueAsync(Guid articleId, string? reason, CancellationToken ct = default)
    {
        var sql = @"
            INSERT INTO knowledge_embedding_queue
                (id, article_id, status, attempts, available_at, reason, created_at, updated_at)
            VALUES
                (@id, @article_id, @status, 0, now(), @reason, now(), now())
            ON CONFLICT (article_id)
            DO UPDATE SET
                status = EXCLUDED.status,
                attempts = 0,
                available_at = now(),
                reason = EXCLUDED.reason,
                last_error = NULL,
                updated_at = now();";

        var parameters = new[]
        {
            new NpgsqlParameter("id", Guid.NewGuid()),
            new NpgsqlParameter("article_id", articleId),
            new NpgsqlParameter("status", KnowledgeEmbeddingQueueStatus.Pending),
            new NpgsqlParameter("reason", (object?)reason ?? DBNull.Value)
        };

        await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        await db.Database.ExecuteSqlRawAsync($"NOTIFY {NotificationChannel};", cancellationToken: ct);
    }

    public async Task<List<KnowledgeEmbeddingQueueItem>> ClaimBatchAsync(int limit, CancellationToken ct = default)
    {
        var sql = @"
            UPDATE knowledge_embedding_queue
            SET status = @processing,
                attempts = attempts + 1,
                updated_at = now()
            WHERE id IN (
                SELECT id
                FROM knowledge_embedding_queue
                WHERE status IN (@pending, @failed)
                  AND available_at <= now()
                ORDER BY updated_at ASC
                FOR UPDATE SKIP LOCKED
                LIMIT @limit
            )
            RETURNING id, article_id, status, attempts, available_at, last_error, reason, created_at, updated_at;";

        var parameters = new[]
        {
            new NpgsqlParameter("processing", KnowledgeEmbeddingQueueStatus.Processing),
            new NpgsqlParameter("pending", KnowledgeEmbeddingQueueStatus.Pending),
            new NpgsqlParameter("failed", KnowledgeEmbeddingQueueStatus.Failed),
            new NpgsqlParameter("limit", limit)
        };

        return await db.KnowledgeEmbeddingQueueItems
            .FromSqlRaw(sql, parameters)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task MarkDoneAsync(Guid id, CancellationToken ct = default)
    {
        var sql = @"
            UPDATE knowledge_embedding_queue
            SET status = @status,
                last_error = NULL,
                updated_at = now()
            WHERE id = @id;";

        var parameters = new[]
        {
            new NpgsqlParameter("status", KnowledgeEmbeddingQueueStatus.Done),
            new NpgsqlParameter("id", id)
        };

        await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
    }

    public async Task MarkFailedAsync(Guid id, string errorMessage, TimeSpan retryDelay, CancellationToken ct = default)
    {
        var sql = @"
            UPDATE knowledge_embedding_queue
            SET status = @status,
                last_error = @error,
                available_at = now() + (@delay_seconds || ' seconds')::interval,
                updated_at = now()
            WHERE id = @id;";

        var trimmed = errorMessage.Length > 2000
            ? errorMessage[..2000]
            : errorMessage;

        var parameters = new[]
        {
            new NpgsqlParameter("status", KnowledgeEmbeddingQueueStatus.Failed),
            new NpgsqlParameter("error", trimmed),
            new NpgsqlParameter("delay_seconds", (int)Math.Max(1, retryDelay.TotalSeconds)),
            new NpgsqlParameter("id", id)
        };

        await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
    }
}
