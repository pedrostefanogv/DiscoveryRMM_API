using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface IAiChatJobRepository
{
    Task<AiChatJob> CreateAsync(AiChatJob job, CancellationToken ct = default);
    Task<AiChatJob?> GetByIdAsync(Guid jobId, Guid agentId, CancellationToken ct = default);
    Task<IReadOnlyList<AiChatJob>> GetRecoverableAsync(int limit, CancellationToken ct = default);
    Task UpdateAsync(AiChatJob job, CancellationToken ct = default);
}
