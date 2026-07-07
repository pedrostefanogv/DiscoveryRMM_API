using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de domínio para merge de tickets e relações entre tickets.
/// </summary>
public interface ITicketMergeService
{
    /// <summary>
    /// Realiza o merge de um ticket source em um ticket target.
    /// Copia comentários, anexos, activity logs e watchers.
    /// Marca o source como fechado/merged.
    /// </summary>
    Task<TicketMergeRecord> MergeAsync(
        Guid sourceTicketId,
        Guid targetTicketId,
        string? mergedBy,
        string? reason,
        CancellationToken ct = default);
}
