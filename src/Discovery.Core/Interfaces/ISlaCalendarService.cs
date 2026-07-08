using Discovery.Core.Entities;

namespace Discovery.Core.Interfaces;

public interface ISlaCalendarService
{
    Task<SlaCalendar?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SlaCalendar>> GetAllAsync(Guid? clientId = null, CancellationToken ct = default);
    Task<SlaCalendar> CreateAsync(SlaCalendar calendar, CancellationToken ct = default);
    Task UpdateAsync(SlaCalendar calendar, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
