using Meduza.Core.Enums;

namespace Meduza.Core.Entities;

/// <summary>
/// Log de atividades de um ticket - auditoria completa.
/// Registra todas as mudanças: estado, atribuição, comentários, SLA, etc.
/// </summary>
public class TicketActivityLog
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    
    /// <summary>
    /// Tipo de atividade ocorrida.
    /// </summary>
    public TicketActivityType Type { get; set; }
    
    /// <summary>
    /// Usuário que realizou a atividade. Null se foi ação automática (sistema).
    /// </summary>
    public Guid? ChangedByUserId { get; set; }
    
    /// <summary>
    /// Valor anterior (antes da mudança).
    /// </summary>
    public string? OldValue { get; set; }
    
    /// <summary>
    /// Valor novo (depois da mudança).
    /// </summary>
    public string? NewValue { get; set; }
    
    /// <summary>
    /// Comentário adicional sobre a atividade.
    /// </summary>
    public string? Comment { get; set; }
    
    public DateTime CreatedAt { get; set; }
}
