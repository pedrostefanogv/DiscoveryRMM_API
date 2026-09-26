namespace Discovery.Core.Enums;

/// <summary>
/// Tipo de entidade de configuração para auditoria.
/// </summary>
public enum ConfigurationEntityType
{
    Server = 0,
    Client = 1,
    Site = 2,

    /// <summary>Calendário de SLA.</summary>
    SlaCalendar = 3,

    /// <summary>Feriado de um calendário de SLA.</summary>
    SlaCalendarHoliday = 4,

    /// <summary>Regra de escalonamento de SLA.</summary>
    TicketEscalationRule = 5
}
