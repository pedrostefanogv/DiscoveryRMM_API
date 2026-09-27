using Discovery.Core.Enums;

namespace Discovery.Core.DTOs;

/// <summary>
/// Evento da timeline de auditoria de um chamado. Diferente da entidade
/// `TicketActivityLog`, expõe o tipo como `activityType`, já traz os valores
/// resolvidos (nomes em vez de GUIDs) e uma descrição legível para o console.
/// </summary>
public sealed record TicketTimelineEntryDto(
    Guid Id,
    Guid TicketId,
    TicketActivityType ActivityType,
    Guid? ChangedByUserId,
    string? ChangedByName,
    string? OldValue,
    string? NewValue,
    string? OldLabel,
    string? NewLabel,
    string? Comment,
    string? Description,
    DateTime CreatedAt);
