namespace Discovery.Core.Entities;

/// <summary>
/// Define o calendário de horas úteis para cálculo de SLA.
/// WorkDayStartHour e WorkDayEndHour são horas do dia (0-23).
/// WorkDaysJson: JSON array de DayOfWeek (0=Sunday..6=Saturday), ex: [1,2,3,4,5].
/// </summary>
public class SlaCalendar
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? ClientId { get; set; }
    public string Timezone { get; set; } = "UTC";
    public int WorkDayStartHour { get; set; } = 8;
    public int WorkDayEndHour { get; set; } = 18;
    public string WorkDaysJson { get; set; } = "[1,2,3,4,5]"; // Seg-Sex por padrão
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<SlaCalendarHoliday> Holidays { get; set; } = new List<SlaCalendarHoliday>();
}
