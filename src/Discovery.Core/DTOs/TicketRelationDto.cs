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
    string? Direction = null,
    // Dados do OUTRO chamado (aditivo) para o console exibir título/status em
    // vez do GUID. Nulo quando o chamado vinculado não pôde ser resolvido.
    Guid? OtherTicketId = null,
    string? OtherTicketTitle = null,
    bool? OtherTicketIsClosed = null
);
