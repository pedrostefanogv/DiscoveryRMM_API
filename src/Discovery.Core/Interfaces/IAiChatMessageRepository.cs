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
    /// Busca mensagens (user/assistant) em conversas ANTERIORES da mesma máquina
    /// (agent_id), por termo. Usada pela tool MCP "memory.search" como memória
    /// persistente: a conversa atual é excluída (ela já está no contexto do LLM).
    /// </summary>
    Task<List<AiChatMessage>> SearchByAgentAsync(
        Guid agentId, string query, int limit, Guid? excludeSessionId = null, CancellationToken ct = default);

    /// <summary>
    /// Retorna a contagem de mensagens + soma de tokens estimados de toda a conversa.
    /// Mais eficiente que carregar todas as mensagens em memória.
    /// </summary>
    Task<(int MessageCount, int EstimatedTokens)> GetStatsAsync(Guid sessionId, CancellationToken ct = default);
}
