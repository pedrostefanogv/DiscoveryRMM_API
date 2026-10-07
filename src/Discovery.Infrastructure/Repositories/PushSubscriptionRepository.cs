using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Repositories;

public class PushSubscriptionRepository : IPushSubscriptionRepository
{
    private readonly DiscoveryDbContext _db;

    public PushSubscriptionRepository(DiscoveryDbContext db) => _db = db;

    public async Task<IReadOnlyList<PushSubscription>> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.PushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.UserId == userId)
            .OrderByDescending(subscription => subscription.LastSeenAt)
            .ToListAsync(ct);
    }

    public async Task<PushSubscription> UpsertAsync(PushSubscription subscription, CancellationToken ct = default)
    {
        var existing = await _db.PushSubscriptions
            .FirstOrDefaultAsync(item => item.Endpoint == subscription.Endpoint, ct);

        if (existing is not null)
        {
            Apply(existing, subscription);
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        _db.PushSubscriptions.Add(subscription);

        try
        {
            await _db.SaveChangesAsync(ct);
            return subscription;
        }
        catch (DbUpdateException)
        {
            // Corrida: outro request inseriu o mesmo endpoint entre o SELECT e o INSERT.
            // O indice unico garante a consistencia; aqui so reaproveitamos a linha vencedora.
            _db.Entry(subscription).State = EntityState.Detached;

            var winner = await _db.PushSubscriptions
                .FirstOrDefaultAsync(item => item.Endpoint == subscription.Endpoint, ct);

            if (winner is null)
                throw;

            Apply(winner, subscription);
            await _db.SaveChangesAsync(ct);
            return winner;
        }
    }

    public async Task<bool> DeleteByEndpointAsync(Guid userId, string endpoint, CancellationToken ct = default)
    {
        var removed = await _db.PushSubscriptions
            .Where(item => item.UserId == userId && item.Endpoint == endpoint)
            .ExecuteDeleteAsync(ct);

        return removed > 0;
    }

    public async Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return 0;

        return await _db.PushSubscriptions
            .Where(item => ids.Contains(item.Id))
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> TrimAsync(Guid userId, int maxSubscriptions, CancellationToken ct = default)
    {
        if (maxSubscriptions <= 0)
            return 0;

        var stale = await _db.PushSubscriptions
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.LastSeenAt)
            .Skip(maxSubscriptions)
            .Select(item => item.Id)
            .ToListAsync(ct);

        return await DeleteByIdsAsync(stale, ct);
    }

    public Task<int> CountByUserIdAsync(Guid userId, CancellationToken ct = default)
        => _db.PushSubscriptions.CountAsync(item => item.UserId == userId, ct);

    /// <summary>
    /// O navegador pertence a quem esta logado nele: reaponta a inscricao para o
    /// usuario atual e atualiza as chaves rotacionadas pelo browser.
    /// </summary>
    private static void Apply(PushSubscription target, PushSubscription source)
    {
        target.UserId = source.UserId;
        target.P256dh = source.P256dh;
        target.Auth = source.Auth;
        target.UserAgent = source.UserAgent;
        target.LastSeenAt = source.LastSeenAt;
    }
}
