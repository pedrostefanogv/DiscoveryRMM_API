using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de domínio para gerenciar relações entre tickets.
/// </summary>
public interface ITicketRelationService
{
    /// <summary>Cria uma relação entre dois tickets (validação de ciclos).</summary>
    Task<TicketRelation> CreateRelationAsync(
        Guid sourceTicketId,
        Guid targetTicketId,
        Enums.TicketRelationType relationType,
        string? createdBy,
        CancellationToken ct = default);

    /// <summary>Lista todas as relações de um ticket.</summary>
    Task<List<TicketRelation>> GetRelationsAsync(Guid ticketId, CancellationToken ct = default);

    /// <summary>Remove uma relação.</summary>
    Task RemoveRelationAsync(Guid relationId, CancellationToken ct = default);
}
