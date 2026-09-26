using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ISlaCalendarRepository
{
    Task<SlaCalendar?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SlaCalendar>> GetAllAsync(Guid? clientId = null, CancellationToken ct = default);

    /// <summary>
    /// Contagem de feriados por calendário, para a listagem não precisar hidratar
    /// todos os feriados só para exibir o número.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetHolidayCountsAsync(Guid? clientId = null, CancellationToken ct = default);
    Task<SlaCalendar> CreateAsync(SlaCalendar calendar, CancellationToken ct = default);
    Task UpdateAsync(SlaCalendar calendar, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Remove a marca de padrão dos demais calendários do mesmo escopo.</summary>
    Task ClearDefaultFlagAsync(Guid? clientId, Guid exceptId, CancellationToken ct = default);

    Task<SlaCalendarHoliday> AddHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default);
    Task UpdateHolidayAsync(SlaCalendarHoliday holiday, CancellationToken ct = default);
    Task DeleteHolidayAsync(Guid holidayId, CancellationToken ct = default);
}
