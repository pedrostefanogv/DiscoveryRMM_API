namespace Discovery.Core.Entities;

/// <summary>
/// Feriado ou dia de exceção de um SlaCalendar.
/// O SLA não avança em dias marcados como feriado.
/// </summary>
public class SlaCalendarHoliday
{
    public Guid Id { get; set; }
    public Guid CalendarId { get; set; }
    public DateTime Date { get; set; }
    public string Name { get; set; } = string.Empty;

    public SlaCalendar? Calendar { get; set; }
}
