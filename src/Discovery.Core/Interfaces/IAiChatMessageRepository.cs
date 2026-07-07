using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAiChatMessageRepository
{
    Task<AiChatMessage> CreateAsync(AiChatMessage message, CancellationToken ct = default);

    /// <summary>
    /// Cria múltiplas mensagens em uma única transação (ex: user + assistant atômico).
    /// </summary>
    Task CreateBatchAsync(IReadOnlyList<AiChatMessage> messages, CancellationToken ct = default);

    Task<List<AiChatMessage>> GetRecentBySessionAsync(Guid sessionId, int limit, CancellationToken ct = default);

    /// <summary>
    /// Retorna a contagem de mensagens + soma de tokens estimados de toda a conversa.
    /// Mais eficiente que carregar todas as mensagens em memória.
    /// </summary>
    Task<(int MessageCount, int EstimatedTokens)> GetStatsAsync(Guid sessionId, CancellationToken ct = default);
}
