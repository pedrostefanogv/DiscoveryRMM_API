namespace Meduza.Core.Interfaces;

public interface ISlaService
{
    /// <summary>
    /// Calcula a data/hora de expiração do SLA baseado no perfil de workflow.
    /// </summary>
    Task<DateTime> CalculateSlaExpiryAsync(Guid workflowProfileId, DateTime createdAt);
    
    /// <summary>
    /// Obtém o status atual do SLA de um ticket.
    /// Retorna: (HorasRestantes, PercentualUsado [0-100], Expirado)
    /// </summary>
    Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId);
    
    /// <summary>
    /// Verifica e registra uma violação de SLA se aplicável.
    /// Retorna true se o SLA foi violado nesta verificação.
    /// </summary>
    Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId);
}
