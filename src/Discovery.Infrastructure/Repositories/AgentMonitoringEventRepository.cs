using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AgentMonitoringEventRepository : IAgentMonitoringEventRepository
{
    private readonly DiscoveryDbContext _db;

    public AgentMonitoringEventRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AgentMonitoringEvent?> GetByIdAsync(Guid id)
    {
        return await _db.AgentMonitoringEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(monitoringEvent => monitoringEvent.Id == id);
    }

    public async Task<AgentMonitoringEvent> CreateAsync(AgentMonitoringEvent monitoringEvent)
    {
        monitoringEvent.Id = monitoringEvent.Id == Guid.Empty ? IdGenerator.NewId() : monitoringEvent.Id;
        monitoringEvent.CreatedAt = DateTime.UtcNow;
        monitoringEvent.OccurredAt = monitoringEvent.OccurredAt == default ? DateTime.UtcNow : monitoringEvent.OccurredAt;
        _db.AgentMonitoringEvents.Add(monitoringEvent);
        await _db.SaveChangesAsync();
        return monitoringEvent;
    }
}