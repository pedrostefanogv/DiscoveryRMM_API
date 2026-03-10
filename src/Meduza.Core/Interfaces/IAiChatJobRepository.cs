using Meduza.Core.Entities;

namespace Meduza.Core.Interfaces;

public interface IAiChatJobRepository
{
    Task<AiChatJob> CreateAsync(AiChatJob job, CancellationToken ct = default);
    Task<AiChatJob?> GetByIdAsync(Guid jobId, Guid agentId, CancellationToken ct = default);
    Task UpdateAsync(AiChatJob job, CancellationToken ct = default);
}
