using Discovery.Core.Entities.Identity;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces.Identity;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class UserRepository : IUserRepository, IUserScopedQueryRepository
{
    private readonly DiscoveryDbContext _db;
    public UserRepository(DiscoveryDbContext db) => _db = db;

    public Task<User?> GetByIdAsync(Guid id)
        => _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);

    public async Task<IReadOnlyList<User>> GetByIdsAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return [];

        return await _db.Users.AsNoTracking()
            .Where(u => idList.Contains(u.Id))
            .ToListAsync();
    }

    public Task<User?> GetByLoginAsync(string login)
        => _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Login == login);

    public Task<User?> GetByEmailAsync(string email)
        => _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email);

    // Comparação case-insensitive: no PostgreSQL o operador '=' é case-sensitive,
    // então "Admin" não encontraria o usuário cadastrado como "admin".
    // LOWER() de ambos os lados traduz para SQL nativo e funciona em qualquer provider.
    public Task<User?> GetByLoginOrEmailAsync(string loginOrEmail)
    {
        var normalized = loginOrEmail.Trim().ToLowerInvariant();
        return _db.Users.AsNoTracking().SingleOrDefaultAsync(
            u => u.Login.ToLower() == normalized || (u.Email != null && u.Email.ToLower() == normalized));
    }

    public async Task<IReadOnlyList<User>> GetAllPageAsync(string? cursor, int take = 50)
    {
        var query = _db.Users.AsNoTracking();
        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            query = CursorPaginationHelper.ApplyCreatedAtCursor(
                query, cursorCreatedAtUtc, cursorId, u => u.CreatedAt, u => u.Id);
        }
        var safeTake = Math.Clamp(take, 1, 200);
        return await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .Take(safeTake + 1)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<User>> GetScopedPageAsync(
        bool hasGlobalAccess,
        IReadOnlyList<Guid> allowedClientIds,
        IReadOnlyList<Guid> allowedSiteIds,
        string? cursor,
        int take = 50)
    {
        var query = ApplyScopeFilter(_db.Users.AsNoTracking(), hasGlobalAccess, allowedClientIds, allowedSiteIds);

        if (CursorPaginationHelper.TryDecodeCreatedAtCursor(cursor, out var cursorCreatedAtUtc, out var cursorId))
        {
            query = CursorPaginationHelper.ApplyCreatedAtCursor(
                query, cursorCreatedAtUtc, cursorId, u => u.CreatedAt, u => u.Id);
        }

        var safeTake = Math.Clamp(take, 1, 200);
        return await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .Take(safeTake + 1)
            .ToListAsync();
    }

    public Task<int> CountScopedAsync(
        bool hasGlobalAccess,
        IReadOnlyList<Guid> allowedClientIds,
        IReadOnlyList<Guid> allowedSiteIds)
        => ApplyScopeFilter(_db.Users.AsNoTracking(), hasGlobalAccess, allowedClientIds, allowedSiteIds).CountAsync();

    public Task<bool> IsUserInScopeAsync(
        bool hasGlobalAccess,
        IReadOnlyList<Guid> allowedClientIds,
        IReadOnlyList<Guid> allowedSiteIds,
        Guid targetUserId)
        // Mesmo predicado da listagem (ApplyScopeFilter): o que o ator não vê na
        // lista, também não pode administrar pelo id. Manter uma única origem evita
        // a divergência clássica entre "listar" e "operar".
        => ApplyScopeFilter(_db.Users.AsNoTracking(), hasGlobalAccess, allowedClientIds, allowedSiteIds)
            .AnyAsync(u => u.Id == targetUserId);

    /// <summary>
    /// Row-level security da listagem de usuários.
    ///
    /// Um usuário é VISÍVEL para um administrador escopado quando possui ao menos
    /// uma atribuição de papel cujo escopo está contido no escopo do administrador:
    /// atribuição Client cujo <c>scope_id</c> está nos clientes permitidos, ou
    /// atribuição Site cujo <c>scope_id</c> está nos sites permitidos (ou cujo
    /// cliente esteja nos clientes permitidos).
    /// </summary>
    private IQueryable<User> ApplyScopeFilter(
        IQueryable<User> query,
        bool hasGlobalAccess,
        IReadOnlyList<Guid> allowedClientIds,
        IReadOnlyList<Guid> allowedSiteIds)
    {
        if (hasGlobalAccess)
            return query;

        var clientIds = allowedClientIds.Distinct().ToArray();
        var siteIds = allowedSiteIds.Distinct().ToArray();

        if (clientIds.Length == 0 && siteIds.Length == 0)
        {
            // Sem escopo concedido: nega em vez de vazar (deny-by-default).
            return query.Where(u => false);
        }

        // Ids de grupos que possuem ao menos uma atribuição dentro do escopo.
        var groupsInScope = _db.UserGroupRoles
            .Where(r =>
                (r.ScopeLevel == Discovery.Core.Enums.Identity.ScopeLevel.Client
                    && r.ScopeId != null && clientIds.Contains(r.ScopeId.Value))
                || (r.ScopeLevel == Discovery.Core.Enums.Identity.ScopeLevel.Site
                    && r.ScopeId != null && siteIds.Contains(r.ScopeId.Value)))
            .Select(r => r.GroupId);

        var userIdsInScope = _db.UserGroupMemberships
            .Where(m => groupsInScope.Contains(m.GroupId))
            .Select(m => m.UserId);

        return query.Where(u => userIdsInScope.Contains(u.Id));
    }

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

    public async Task<bool> SetMfaFailureStateAsync(Guid userId, int failedAttempts, DateTime? lockoutUntil)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.MfaFailedAttempts, failedAttempts)
                .SetProperty(u => u.MfaLockoutUntil, lockoutUntil)
                .SetProperty(u => u.UpdatedAt, now));
        return rows > 0;
    }

    public async Task<bool> ResetMfaFailureStateAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.MfaFailedAttempts, 0)
                .SetProperty(u => u.MfaLockoutUntil, (DateTime?)null)
                .SetProperty(u => u.UpdatedAt, now));
        return rows > 0;
    }

    // Case-insensitive: alinhado com as comparações OrdinalIgnoreCase do
    // CompleteFirstAccessAsync — evita criar "ADMIN" quando "admin" já existe.
    public Task<bool> ExistsByLoginAsync(string login)
        => _db.Users.AnyAsync(u => u.Login.ToLower() == login.Trim().ToLowerInvariant());

    public Task<bool> ExistsByEmailAsync(string email)
        => _db.Users.AnyAsync(u => u.Email.ToLower() == email.Trim().ToLowerInvariant());

    public Task<int> CountAsync()
        => _db.Users.CountAsync();
}
