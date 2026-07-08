using Discovery.Core.Cqrs.Tickets.Dtos;
using Discovery.Core.Cqrs.Tickets.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Serviço de aplicação para queries de Tickets.
/// Encapsula consultas otimizadas (EF Core AsNoTracking / Dapper)
/// para manter os query handlers thin.
/// </summary>
public interface ITicketQueryService
{
    /// <summary>Lista tickets com cursor pagination e filtros.</summary>
    Task<CursorPageDto<TicketListItemDto>> ListTicketsAsync(
        TicketFilterQuery filter, CancellationToken ct = default);

    /// <summary>Obtém detalhes de um ticket por ID.</summary>
    Task<TicketDetailDto?> GetTicketByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lista comentários de um ticket com cursor pagination.</summary>
    Task<CursorPageDto<TicketCommentDto>> GetCommentsAsync(
        Guid ticketId, string? cursor, int limit, CancellationToken ct = default);
}
