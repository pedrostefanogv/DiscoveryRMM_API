using Discovery.Core.ValueObjects;

namespace Discovery.Core.Interfaces;

public interface IAutoTicketDedupService
{
    Task<AutoTicketDedupResult> TryAcquireOrGetAsync(string dedupKey, TimeSpan dedupWindow, CancellationToken cancellationToken = default);
    Task RegisterCreatedTicketAsync(string dedupKey, Guid ticketId, CancellationToken cancellationToken = default);
}