using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class ClientRepository : IClientRepository
{
    private readonly MeduzaDbContext _db;

    public ClientRepository(MeduzaDbContext db) => _db = db;

    public async Task<Client?> GetByIdAsync(Guid id)
    {
        return await _db.Clients
            .AsNoTracking()
            .SingleOrDefaultAsync(client => client.Id == id);
    }

    public async Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false)
    {
        IQueryable<Client> query = _db.Clients.AsNoTracking();

        if (!includeInactive)
            query = query.Where(client => client.IsActive);

        return await query
            .OrderBy(client => client.Name)
            .ToListAsync();
    }

    public async Task<Client> CreateAsync(Client client)
    {
        client.Id = IdGenerator.NewId();
        client.CreatedAt = DateTime.UtcNow;
        client.UpdatedAt = DateTime.UtcNow;

        _db.Clients.Add(client);
        await _db.SaveChangesAsync();
        return client;
    }

    public async Task UpdateAsync(Client client)
    {
        var existingClient = await _db.Clients.SingleOrDefaultAsync(existing => existing.Id == client.Id);
        if (existingClient is null)
            return;

        existingClient.Name = client.Name;
        existingClient.Notes = client.Notes;
        existingClient.IsActive = client.IsActive;
        existingClient.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var now = DateTime.UtcNow;

        await _db.Clients
            .Where(client => client.Id == id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(client => client.IsActive, _ => false)
                .SetProperty(client => client.UpdatedAt, _ => now));
    }
}
