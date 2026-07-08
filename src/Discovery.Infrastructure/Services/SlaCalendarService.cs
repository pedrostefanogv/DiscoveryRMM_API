using Discovery.Core.Entities;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

public sealed class SlaCalendarService : ISlaCalendarService
{
    private readonly ISlaCalendarRepository _repo;
    public SlaCalendarService(ISlaCalendarRepository repo) => _repo = repo;

    public Task<SlaCalendar?> GetByIdAsync(Guid id, CancellationToken ct = default) => _repo.GetByIdAsync(id, ct);
    public Task<IReadOnlyList<SlaCalendar>> GetAllAsync(Guid? clientId = null, CancellationToken ct = default) => _repo.GetAllAsync(clientId, ct);
    public Task<SlaCalendar> CreateAsync(SlaCalendar calendar, CancellationToken ct = default) => _repo.CreateAsync(calendar, ct);
    public Task UpdateAsync(SlaCalendar calendar, CancellationToken ct = default) => _repo.UpdateAsync(calendar, ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _repo.DeleteAsync(id, ct);
}
