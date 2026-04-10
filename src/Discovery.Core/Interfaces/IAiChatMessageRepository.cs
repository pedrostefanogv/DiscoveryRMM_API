using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAiChatMessageRepository
{
    Task<AiChatMessage> CreateAsync(AiChatMessage message, CancellationToken ct = default);
    Task<List<AiChatMessage>> GetRecentBySessionAsync(Guid sessionId, int limit, CancellationToken ct = default);
}
