using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ISlaService
{
    /// <summary>
    /// Calcula a data/hora de expiração do SLA de resolução baseado no perfil de workflow.
    /// </summary>
    Task<DateTime> CalculateSlaExpiryAsync(Guid workflowProfileId, DateTime createdAt);

    /// <summary>
    /// Calcula a data/hora de expiração do SLA de primeira resposta baseado no perfil de workflow.
    /// </summary>
    Task<DateTime> CalculateFirstResponseExpiryAsync(Guid workflowProfileId, DateTime createdAt);

    /// <summary>
    /// Obtém o status atual do SLA de resolução de um ticket, considerando pausas.
    /// Retorna: (HorasRestantes, PercentualUsado [0-100], Expirado)
    /// </summary>
    Task<(int HoursRemaining, double PercentUsed, bool Breached)> GetSlaStatusAsync(Guid ticketId);

    /// <summary>
    /// Obtém o status atual do SLA de primeira resposta de um ticket.
    /// Retorna: (HorasRestantes, PercentualUsado [0-100], Breached, Achieved)
    /// </summary>
    Task<(int HoursRemaining, double PercentUsed, bool Breached, bool Achieved)> GetFrtStatusAsync(Guid ticketId);

    /// <summary>
    /// Calcula a expiração efetiva do SLA, descontando segundos pausados.
    /// </summary>
    DateTime? GetEffectiveSlaExpiry(Ticket ticket);

    /// <summary>
    /// Verifica e registra uma violação de SLA se aplicável.
    /// Retorna true se o SLA foi violado nesta verificação.
    /// </summary>
    Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId);
}
