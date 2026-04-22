using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Infrastructure.Services;

public class AutoTicketDedupService : IAutoTicketDedupService
{
    private readonly IAlertCorrelationLockRepository _correlationLockRepository;

    public AutoTicketDedupService(IAlertCorrelationLockRepository correlationLockRepository)
    {
        _correlationLockRepository = correlationLockRepository;
    }

    public async Task<AutoTicketDedupResult> TryAcquireOrGetAsync(string dedupKey, TimeSpan dedupWindow, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(dedupWindow <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : dedupWindow);
        var existing = await _correlationLockRepository.GetByDedupKeyAsync(dedupKey);

        if (existing is not null && existing.ExpiresAt > now)
        {
            return new AutoTicketDedupResult
            {
                DedupKey = dedupKey,
                Acquired = false,
                ExistingTicketId = existing.LastTicketId,
                ExpiresAt = existing.ExpiresAt
            };
        }

        if (existing is not null)
        {
            existing.ExpiresAt = expiresAt;
            existing.LastAlertAt = now;
            existing.LastTicketId = null;
            await _correlationLockRepository.UpdateAsync(existing);

            return new AutoTicketDedupResult
            {
                DedupKey = dedupKey,
                Acquired = true,
                ExpiresAt = expiresAt
            };
        }

        try
        {
            await _correlationLockRepository.CreateAsync(new AlertCorrelationLock
            {
                DedupKey = dedupKey,
                ExpiresAt = expiresAt,
                LastAlertAt = now
            });

            return new AutoTicketDedupResult
            {
                DedupKey = dedupKey,
                Acquired = true,
                ExpiresAt = expiresAt
            };
        }
        catch (DbUpdateException)
        {
            var concurrentLock = await _correlationLockRepository.GetByDedupKeyAsync(dedupKey);
            return new AutoTicketDedupResult
            {
                DedupKey = dedupKey,
                Acquired = false,
                ExistingTicketId = concurrentLock?.LastTicketId,
                ExpiresAt = concurrentLock?.ExpiresAt
            };
        }
    }

    public async Task RegisterCreatedTicketAsync(string dedupKey, Guid ticketId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var existing = await _correlationLockRepository.GetByDedupKeyAsync(dedupKey);
        if (existing is null)
            return;

        existing.LastTicketId = ticketId;
        existing.LastAlertAt = DateTime.UtcNow;
        await _correlationLockRepository.UpdateAsync(existing);
    }
}