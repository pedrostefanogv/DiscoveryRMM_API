using Discovery.Core.Entities.Security;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Security;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class UserMfaKeyRepository : IUserMfaKeyRepository
{
    private readonly DiscoveryDbContext _db;
    public UserMfaKeyRepository(DiscoveryDbContext db) => _db = db;

    public Task<UserMfaKey?> GetByIdAsync(Guid id)
        => _db.UserMfaKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id);

    public async Task<IEnumerable<UserMfaKey>> GetActiveByUserIdAsync(Guid userId)
        => await _db.UserMfaKeys.AsNoTracking()
            .Where(k => k.UserId == userId && k.IsActive)
            .OrderBy(k => k.CreatedAt)
            .ToListAsync();

    public Task<UserMfaKey?> GetByCredentialIdAsync(string credentialIdBase64)
        => _db.UserMfaKeys.AsNoTracking()
            .SingleOrDefaultAsync(k => k.CredentialIdBase64 == credentialIdBase64 && k.IsActive);

    public async Task<UserMfaKey> CreateAsync(UserMfaKey key)
    {
        if (key.Id == Guid.Empty) key.Id = IdGenerator.NewId();
        key.CreatedAt = DateTime.UtcNow;
        _db.UserMfaKeys.Add(key);
        await _db.SaveChangesAsync();
        return key;
    }

    public async Task<bool> UpdateSignCountAsync(Guid keyId, uint newSignCount)
    {
        var rows = await _db.UserMfaKeys
            .Where(k => k.Id == keyId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.SignCount, newSignCount));
        return rows > 0;
    }

    public async Task<bool> UpdateLastUsedAsync(Guid keyId)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.UserMfaKeys
            .Where(k => k.Id == keyId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now));
        return rows > 0;
    }

    public async Task<bool> DeactivateAsync(Guid keyId, Guid userId)
    {
        var rows = await _db.UserMfaKeys
            .Where(k => k.Id == keyId && k.UserId == userId && k.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.IsActive, false));
        return rows > 0;
    }

    public Task<int> DeactivateAllByUserIdAsync(Guid userId)
        => _db.UserMfaKeys
            .Where(k => k.UserId == userId && k.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.IsActive, false));

    public Task<int> CountActiveByUserIdAsync(Guid userId)
        => _db.UserMfaKeys.CountAsync(k => k.UserId == userId && k.IsActive);

    public async Task<bool> RenameAsync(Guid keyId, Guid userId, string newName)
    {
        var rows = await _db.UserMfaKeys
            .Where(k => k.Id == keyId && k.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Name, newName));
        return rows > 0;
    }
}
