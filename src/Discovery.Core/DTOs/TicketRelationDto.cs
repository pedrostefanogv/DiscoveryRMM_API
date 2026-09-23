namespace Discovery.Core.DTOs;

/// <summary>
/// Representação de uma relação entre chamados para o console. Direction indica
/// se o chamado consultado é a origem ("source"), o destino ("target") ou ambos.
/// </summary>
public sealed record TicketRelationDto(
    Guid Id,
    Guid SourceTicketId,
    Guid TargetTicketId,
    string RelationType,
    string? CreatedBy,
    DateTime CreatedAt,
    string? Direction = null
);
