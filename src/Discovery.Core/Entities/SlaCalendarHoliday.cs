namespace Discovery.Core.Entities;

/// <summary>
/// Tipo do feriado.
/// </summary>
public enum HolidayType
{
    /// <summary>Data fixa (não recorre). Ex: feriado municipal específico de 2026.</summary>
    Fixed = 0,
    /// <summary>Recorrente anual (mesmo mês/dia todo ano). Ex: Natal, Ano Novo.</summary>
    Yearly = 1,
    /// <summary>Cálculo relativo (ex: primeira segunda de junho, 5º dia útil de março).</summary>
    Relative = 2,
}

/// <summary>
/// Método de cálculo para feriados relativos.
/// </summary>
public enum RelativeHolidayMethod
{
    /// <summary>Enésimo dia da semana do mês. Ex: 3ª segunda-feira de janeiro.</summary>
    DayOfWeekOccurrence = 0,
    /// <summary>Enésimo dia útil do mês. Ex: 5º dia útil de março.</summary>
    NthBusinessDay = 1,
}

/// <summary>
/// Feriado ou dia de exceção de um SlaCalendar.
/// O SLA não avança em dias marcados como feriado.
/// </summary>
public class SlaCalendarHoliday
{
    public Guid Id { get; set; }
    public Guid CalendarId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Data de referência.
    /// Para Fixed: data exata do feriado.
    /// Para Yearly: data de exemplo (ignora o ano no cálculo, usa apenas mês/dia).
    /// Para Relative: não usada diretamente no cálculo.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>Tipo do feriado. Default = Fixed (comportamento legado).</summary>
    public int HolidayTypeValue { get; set; } = (int)HolidayType.Fixed;

    /// <summary>Mês (1-12) para feriados Relative.</summary>
    public int? RelativeMonth { get; set; }

    /// <summary>Dia da semana (0=Dom..6=Sáb) para Relative DayOfWeekOccurrence.</summary>
    public int? RelativeDayOfWeek { get; set; }

    /// <summary>Ocorrência (1-5, onde 5=última) para Relative.</summary>
    public int? RelativeOccurrence { get; set; }

    /// <summary>Método de cálculo para Relative (0=DayOfWeekOccurrence, 1=NthBusinessDay).</summary>
    public int? RelativeMethodValue { get; set; }

    public HolidayType HolidayType
    {
        get => (HolidayType)HolidayTypeValue;
        set => HolidayTypeValue = (int)value;
    }

    public RelativeHolidayMethod? RelativeMethod
    {
        get => RelativeMethodValue.HasValue ? (RelativeHolidayMethod)RelativeMethodValue.Value : null;
        set => RelativeMethodValue = value.HasValue ? (int)value : null;
    }

    public SlaCalendar? Calendar { get; set; }
}
