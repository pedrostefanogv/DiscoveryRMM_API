using Discovery.Core.Cqrs;
using Discovery.Core.DTOs;
using Discovery.Core.Enums;

namespace Discovery.Core.Cqrs.Tickets.Commands;

/// <summary>Cria uma relação entre o chamado da rota e um chamado alvo.</summary>
public sealed record CreateTicketRelationCommand(
    Guid TicketId,
    Guid TargetTicketId,
    TicketRelationType RelationType,
    Guid? CreatedByUserId,
    string? CreatedBy
) : ICommand<Result<TicketRelationDto>>;

/// <summary>Remove uma relação, desde que pertença ao chamado da rota.</summary>
public sealed record DeleteTicketRelationCommand(
    Guid TicketId,
    Guid RelationId,
    Guid? ChangedByUserId
) : ICommand<Result<VoidResult>>;
