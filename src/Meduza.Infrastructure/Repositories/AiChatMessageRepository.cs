using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AiChatMessageRepository : IAiChatMessageRepository
{
    private readonly MeduzaDbContext _db;

    public AiChatMessageRepository(MeduzaDbContext db) => _db = db;

    public async Task<AiChatMessage> CreateAsync(AiChatMessage message, CancellationToken ct = default)
    {
        message.Id = IdGenerator.NewId();
        message.CreatedAt = DateTime.UtcNow;

        _db.AiChatMessages.Add(message);
        await _db.SaveChangesAsync(ct);
        return message;
    }

    public async Task<List<AiChatMessage>> GetRecentBySessionAsync(Guid sessionId, int limit, CancellationToken ct = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);

        return await _db.AiChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.SequenceNumber)
            .Take(safeLimit)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync(ct);
    }
}
