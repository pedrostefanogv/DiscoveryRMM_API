using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AiChatMessageRepository : IAiChatMessageRepository
{
    private readonly DiscoveryDbContext _db;

    public AiChatMessageRepository(DiscoveryDbContext db) => _db = db;

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
