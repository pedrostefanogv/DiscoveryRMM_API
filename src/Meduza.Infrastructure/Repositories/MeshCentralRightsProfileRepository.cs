using Meduza.Core.Entities;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class MeshCentralRightsProfileRepository : IMeshCentralRightsProfileRepository
{
    private readonly MeduzaDbContext _db;

    public MeshCentralRightsProfileRepository(MeduzaDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<MeshCentralRightsProfile>> GetAllAsync()
        => await _db.MeshCentralRightsProfiles
            .AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync();

    public Task<MeshCentralRightsProfile?> GetByIdAsync(Guid id)
        => _db.MeshCentralRightsProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == id);

    public Task<MeshCentralRightsProfile?> GetByNameAsync(string name)
    {
        var normalized = NormalizeName(name);
        return _db.MeshCentralRightsProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Name == normalized);
    }

    public Task<bool> IsNameInUseAsync(string name, Guid? excludeId = null)
    {
        var normalized = NormalizeName(name);
        return _db.MeshCentralRightsProfiles
            .AsNoTracking()
            .AnyAsync(p => p.Name == normalized && (!excludeId.HasValue || p.Id != excludeId.Value));
    }

    public Task<bool> IsProfileReferencedByRolesAsync(string name)
    {
        var normalized = NormalizeName(name);
        return _db.Roles
            .AsNoTracking()
            .AnyAsync(r => r.MeshRightsProfile != null && r.MeshRightsProfile == normalized);
    }

    public async Task<MeshCentralRightsProfile> CreateAsync(MeshCentralRightsProfile profile)
    {
        if (profile.Id == Guid.Empty)
            profile.Id = IdGenerator.NewId();

        profile.Name = NormalizeName(profile.Name);
        profile.CreatedAt = DateTime.UtcNow;
        profile.UpdatedAt = DateTime.UtcNow;

        _db.MeshCentralRightsProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return profile;
    }

    public async Task<MeshCentralRightsProfile> UpdateAsync(MeshCentralRightsProfile profile)
    {
        profile.Name = NormalizeName(profile.Name);
        profile.UpdatedAt = DateTime.UtcNow;

        _db.MeshCentralRightsProfiles.Update(profile);
        await _db.SaveChangesAsync();
        return profile;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var profile = await _db.MeshCentralRightsProfiles.SingleOrDefaultAsync(p => p.Id == id);
        if (profile is null)
            return false;

        _db.MeshCentralRightsProfiles.Remove(profile);
        await _db.SaveChangesAsync();
        return true;
    }

    private static string NormalizeName(string name)
        => name.Trim().ToLowerInvariant();
}
