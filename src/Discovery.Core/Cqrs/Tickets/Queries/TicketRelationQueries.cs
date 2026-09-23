using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;

namespace Discovery.Core.Cqrs.Tickets.Queries;

/// <summary>Lista as relações (origem e destino) de um chamado.</summary>
public sealed record GetTicketRelationsQuery(Guid TicketId, Guid? TargetTicketId = null) : IQuery<Result<IReadOnlyList<TicketRelationDto>>>;
