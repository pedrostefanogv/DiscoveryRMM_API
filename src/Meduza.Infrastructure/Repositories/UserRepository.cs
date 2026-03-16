using Meduza.Core.Entities.Identity;
using Meduza.Core.Helpers;
using Meduza.Core.Interfaces.Identity;
using Meduza.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Meduza.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly MeduzaDbContext _db;
    public UserRepository(MeduzaDbContext db) => _db = db;

    public Task<User?> GetByIdAsync(Guid id)
        => _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);

    public Task<User?> GetByLoginAsync(string login)
        => _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Login == login);

    public Task<User?> GetByEmailAsync(string email)
        => _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email);

    public Task<User?> GetByLoginOrEmailAsync(string loginOrEmail)
        => _db.Users.AsNoTracking().SingleOrDefaultAsync(
            u => u.Login == loginOrEmail || u.Email == loginOrEmail);

    public async Task<IEnumerable<User>> GetAllAsync(int skip = 0, int take = 50)
        => await _db.Users.AsNoTracking().OrderBy(u => u.FullName).Skip(skip).Take(take).ToListAsync();

    public async Task<User> CreateAsync(User user)
    {
        if (user.Id == Guid.Empty) user.Id = IdGenerator.NewId();
        user.CreatedAt = DateTime.UtcNow;
        user.UpdatedAt = DateTime.UtcNow;
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<User> UpdateAsync(User user)
    {
        var now = DateTime.UtcNow;
        user.UpdatedAt = now;

        var tracked = _db.Users.Local.FirstOrDefault(u => u.Id == user.Id);
        if (tracked is not null)
        {
            _db.Entry(tracked).CurrentValues.SetValues(user);
            tracked.UpdatedAt = now;
        }
        else
        {
            _db.Users.Attach(user);
            _db.Entry(user).State = EntityState.Modified;
            _db.Entry(user).Property(u => u.CreatedAt).IsModified = false;
        }

        await _db.SaveChangesAsync();
        return tracked ?? user;
    }

    public async Task<bool> SetMfaConfiguredAsync(Guid userId, bool configured)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.MfaConfigured, configured)
                .SetProperty(u => u.UpdatedAt, now));
        return rows > 0;
    }

    public async Task<bool> SetLastLoginAsync(Guid userId, DateTime at)
    {
        var rows = await _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, at));
        return rows > 0;
    }

    public Task<bool> ExistsByLoginAsync(string login)
        => _db.Users.AnyAsync(u => u.Login == login);

    public Task<bool> ExistsByEmailAsync(string email)
        => _db.Users.AnyAsync(u => u.Email == email);

    public Task<int> CountAsync()
        => _db.Users.CountAsync();
}
