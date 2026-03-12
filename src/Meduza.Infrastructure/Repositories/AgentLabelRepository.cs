using Meduza.Core.Entities;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class AgentLabelRepository : IAgentLabelRepository
{
    private readonly MeduzaDbContext _db;

    public AgentLabelRepository(MeduzaDbContext db) => _db = db;

    public async Task<IReadOnlyList<AgentLabel>> GetByAgentIdAsync(Guid agentId)
    {
        return await _db.AgentLabels
            .AsNoTracking()
            .Where(label => label.AgentId == agentId)
            .OrderBy(label => label.Label)
            .ToListAsync();
    }
}
