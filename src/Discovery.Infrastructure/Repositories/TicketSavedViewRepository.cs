using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class TicketSavedViewRepository : ITicketSavedViewRepository
{
    private readonly DiscoveryDbContext _db;

    public TicketSavedViewRepository(DiscoveryDbContext db)
    {
        _db = db;
    }

    public async Task<IEnumerable<TicketSavedView>> GetByUserAsync(Guid? userId)
    {
        return await _db.TicketSavedViews
            .AsNoTracking()
            .Where(v => v.UserId == userId || v.IsShared)
            .OrderBy(v => v.Name)
            .ToListAsync();
    }

    public async Task<TicketSavedView?> GetByIdAsync(Guid id)
    {
        return await _db.TicketSavedViews
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == id);
    }

    public async Task<TicketSavedView> CreateAsync(TicketSavedView view)
    {
        view.Id = IdGenerator.NewId();
        view.CreatedAt = DateTime.UtcNow;
        view.UpdatedAt = DateTime.UtcNow;
        _db.TicketSavedViews.Add(view);
        await _db.SaveChangesAsync();
        return view;
    }

    public async Task UpdateAsync(TicketSavedView view)
    {
        view.UpdatedAt = DateTime.UtcNow;
        _db.TicketSavedViews.Update(view);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await _db.TicketSavedViews
            .Where(v => v.Id == id)
            .ExecuteDeleteAsync();
    }
}
