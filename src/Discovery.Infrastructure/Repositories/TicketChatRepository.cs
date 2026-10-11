using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

/// <summary>
/// Persistência do chat de suporte (EF Core / PostgreSQL).
/// </summary>
public class TicketChatRepository(DiscoveryDbContext db) : ITicketChatRepository
{
    private const int SequenceRetryAttempts = 5;

    public Task<TicketChatSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => db.TicketChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    public Task<TicketChatSession?> GetCurrentForTicketAsync(Guid ticketId, CancellationToken cancellationToken = default)
        => db.TicketChatSessions
            .Where(s => s.TicketId == ticketId
                && (s.Status == TicketChatSessionStatus.Pending || s.Status == TicketChatSessionStatus.Active))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<TicketChatSession?> GetCurrentForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
        => db.TicketChatSessions
            .Where(s => s.AgentId == agentId
                && (s.Status == TicketChatSessionStatus.Pending || s.Status == TicketChatSessionStatus.Active))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddSessionAsync(TicketChatSession session, CancellationToken cancellationToken = default)
    {
        db.TicketChatSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateSessionAsync(TicketChatSession session, CancellationToken cancellationToken = default)
    {
        session.UpdatedAt = DateTime.UtcNow;
        db.TicketChatSessions.Update(session);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TicketChatSession>> GetHistoryAsync(Guid ticketId, int skip, int take, CancellationToken cancellationToken = default)
        => await db.TicketChatSessions
            .AsNoTracking()
            .Where(s => s.TicketId == ticketId && s.Status != TicketChatSessionStatus.Pending && s.Status != TicketChatSessionStatus.Active)
            .OrderByDescending(s => s.StartedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TicketChatMessage>> GetMessagesAsync(Guid sessionId, long afterSequence, int limit, CancellationToken cancellationToken = default)
        => await db.TicketChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.SequenceNumber > afterSequence)
            .OrderBy(m => m.SequenceNumber)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

    public async Task<(TicketChatMessage Message, bool Created)> AppendMessageAsync(
        Guid sessionId,
        TicketChatMessage message,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= SequenceRetryAttempts; attempt++)
        {
            try
            {
                return await AppendOnceAsync(sessionId, message, attachmentIds, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < SequenceRetryAttempts)
            {
                // Corrida de sequence_number (ou reentrega simultânea do mesmo
                // client_message_id): o índice único transforma a corrida em erro
                // visível. Limpa o tracker e recalcula.
                db.ChangeTracker.Clear();
                await Task.Delay(25 * attempt, cancellationToken);
            }
        }

        return await AppendOnceAsync(sessionId, message, attachmentIds, cancellationToken);
    }

    private async Task<(TicketChatMessage Message, bool Created)> AppendOnceAsync(
        Guid sessionId,
        TicketChatMessage message,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Idempotência: reentrega do mesmo envelope (at-least-once) não duplica.
        var existing = await db.TicketChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.SessionId == sessionId && m.ClientMessageId == message.ClientMessageId, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (existing, false);
        }

        var nextSequence = (await db.TicketChatMessages
            .Where(m => m.SessionId == sessionId)
            .Select(m => (long?)m.SequenceNumber)
            .MaxAsync(cancellationToken) ?? 0L) + 1L;

        message.SequenceNumber = nextSequence;
        if (message.Id == Guid.Empty)
            message.Id = Guid.NewGuid();

        db.TicketChatMessages.Add(message);

        foreach (var attachmentId in attachmentIds.Distinct())
        {
            db.TicketChatMessageAttachments.Add(new TicketChatMessageAttachment
            {
                Id = Guid.NewGuid(),
                MessageId = message.Id,
                AttachmentId = attachmentId,
                CreatedAt = message.CreatedAt
            });
        }

        var session = await db.TicketChatSessions.FirstAsync(s => s.Id == sessionId, cancellationToken);
        session.MessageCount += 1;
        session.LastMessageAt = message.CreatedAt;
        session.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (message, true);
    }

    public async Task<IReadOnlyList<TicketChatAttachmentRef>> GetAttachmentsForMessagesAsync(
        IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
            return [];

        var ids = messageIds.Distinct().ToArray();

        return await (
            from link in db.TicketChatMessageAttachments.AsNoTracking()
            join attachment in db.Attachments.AsNoTracking() on link.AttachmentId equals attachment.Id
            where ids.Contains(link.MessageId)
            orderby attachment.CreatedAt
            select new TicketChatAttachmentRef(
                link.MessageId,
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                attachment.SizeBytes,
                attachment.UploadedBy,
                attachment.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TicketChatSession>> GetExpiredPendingAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken = default)
        => await db.TicketChatSessions
            .Where(s => s.Status == TicketChatSessionStatus.Pending
                && s.InviteExpiresAt != null
                && s.InviteExpiresAt < nowUtc)
            .OrderBy(s => s.InviteExpiresAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TicketChatSession>> GetInactiveActiveAsync(DateTime cutoffUtc, int limit, CancellationToken cancellationToken = default)
        => await db.TicketChatSessions
            .Where(s => s.Status == TicketChatSessionStatus.Active
                && ((s.LastMessageAt != null && s.LastMessageAt < cutoffUtc)
                    || (s.LastMessageAt == null && s.StartedAt < cutoffUtc)))
            .OrderBy(s => s.LastMessageAt ?? s.StartedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TicketChatSession>> GetActiveForClosedTicketsAsync(int limit, CancellationToken cancellationToken = default)
        => await db.TicketChatSessions
            .Where(s => s.Status == TicketChatSessionStatus.Active
                && db.Tickets.Any(t => t.Id == s.TicketId && t.ClosedAt != null))
            .OrderBy(s => s.StartedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);
}
