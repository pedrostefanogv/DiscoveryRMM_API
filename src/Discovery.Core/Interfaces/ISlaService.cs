using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

/// <summary>
/// Contexto de SLA resolvido para um ticket: calendário (ou null para 24x7) e o
/// limiar percentual de aviso do perfil.
/// </summary>
public sealed record TicketSlaContext(SlaCalendar? Calendar, int WarningThresholdPercent);

public interface ISlaService
{
    /// <summary>Custo padrão do aviso preventivo quando o perfil não define um.</summary>
    const int DefaultWarningThresholdPercent = 80;

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
    /// Status do SLA de resolução sem I/O, para quando o ticket já foi carregado.
    /// </summary>
    (int HoursRemaining, double PercentUsed, bool Breached) GetSlaStatus(Ticket ticket, SlaCalendar? calendar);

    /// <summary>
    /// Obtém o status atual do SLA de primeira resposta de um ticket.
    /// Retorna: (HorasRestantes, PercentualUsado [0-100], Breached, Achieved)
    /// </summary>
    Task<(int HoursRemaining, double PercentUsed, bool Breached, bool Achieved)> GetFrtStatusAsync(Guid ticketId);

    /// <summary>
    /// Resolve calendário e limiar de aviso do perfil do ticket. Permite ao job
    /// de monitoramento consultar uma vez por perfil e reaproveitar em memória.
    /// </summary>
    Task<TicketSlaContext> GetSlaContextForTicketAsync(Ticket ticket);

    /// <summary>
    /// Calcula a expiração efetiva do SLA, descontando segundos pausados.
    /// </summary>
    DateTime? GetEffectiveSlaExpiry(Ticket ticket);

    /// <summary>
    /// Verifica e registra uma violação de SLA se aplicável.
    /// Retorna true se o SLA foi violado nesta verificação.
    /// </summary>
    Task<bool> CheckAndLogSlaBreachAsync(Guid ticketId);

    /// <summary>
    /// Verifica e registra violação reaproveitando o ticket já carregado.
    /// </summary>
    Task<bool> CheckAndLogSlaBreachAsync(Ticket ticket);
}
