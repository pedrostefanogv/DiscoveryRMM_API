using Discovery.Core.Entities;
using Discovery.Core.Enums;

namespace Discovery.Core.Interfaces;

/// <summary>Repositório de relações entre chamados (duplicado, bloqueia, relacionado, pai/filho).</summary>
public interface ITicketRelationRepository
{
    Task<IReadOnlyList<TicketRelation>> GetByTicketAsync(Guid ticketId, CancellationToken ct = default);
    Task<TicketRelation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid sourceTicketId, Guid targetTicketId, TicketRelationType type, CancellationToken ct = default);
    Task<bool> ExistsReverseAsync(Guid sourceTicketId, Guid targetTicketId, CancellationToken ct = default);
    Task<TicketRelation> AddAsync(TicketRelation relation, CancellationToken ct = default);
    Task RemoveAsync(Guid id, CancellationToken ct = default);
}
