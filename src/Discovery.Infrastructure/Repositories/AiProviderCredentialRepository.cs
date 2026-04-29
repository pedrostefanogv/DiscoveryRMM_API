using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class AiProviderCredentialRepository : IAiProviderCredentialRepository
{
    private readonly DiscoveryDbContext _db;

    public AiProviderCredentialRepository(DiscoveryDbContext db) => _db = db;

    public async Task<AiProviderCredential?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.AiProviderCredentials.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<AiProviderCredential?> GetByScopeAsync(Guid? clientId, Guid? siteId, string provider, CancellationToken ct = default)
        => await _db.AiProviderCredentials
            .Where(c => c.Provider == provider)
            .Where(c => c.ClientId == clientId && c.SiteId == siteId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Busca todas as credenciais na hierarquia: Global + Client + Site.
    /// Ordem de precedência: Site > Client > Global.
    /// </summary>
    public async Task<List<AiProviderCredential>> GetByHierarchyAsync(Guid? clientId, Guid? siteId, CancellationToken ct = default)
    {
        var query = _db.AiProviderCredentials.AsQueryable();

        // Global (ScopeType = 0) sempre incluso
        // + Client específico se clientId informado
        // + Site específico se siteId informado
        query = query.Where(c =>
            c.ClientId == null && c.SiteId == null ||          // Global
            (clientId.HasValue && c.ClientId == clientId && c.SiteId == null) ||  // Client
            (siteId.HasValue && c.SiteId == siteId));           // Site

        return await query.ToListAsync(ct);
    }

    public async Task<List<AiProviderCredential>> GetAllAsync(CancellationToken ct = default)
        => await _db.AiProviderCredentials.ToListAsync(ct);

    public async Task<AiProviderCredential> CreateAsync(AiProviderCredential credential, CancellationToken ct = default)
    {
        _db.AiProviderCredentials.Add(credential);
        await _db.SaveChangesAsync(ct);
        return credential;
    }

    public async Task<AiProviderCredential> UpdateAsync(AiProviderCredential credential, CancellationToken ct = default)
    {
        _db.AiProviderCredentials.Update(credential);
        await _db.SaveChangesAsync(ct);
        return credential;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var credential = await _db.AiProviderCredentials.FindAsync([id], ct);
        if (credential is not null)
        {
            _db.AiProviderCredentials.Remove(credential);
            await _db.SaveChangesAsync(ct);
        }
    }
}
