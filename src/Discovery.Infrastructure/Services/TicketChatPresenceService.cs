using System.Collections.Concurrent;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Presenca em memoria do chat de suporte (janela aberta e visivel).
///
/// A API roda como uma unica instancia atras do nginx, entao um dicionario no
/// processo basta e nao depende de Redis estar configurado. Se a API passar a
/// rodar em mais de uma instancia, esta e a classe a trocar por uma
/// implementacao compartilhada (Redis) mantendo a mesma interface.
///
/// A presenca expira por TTL: se o console morrer sem enviar o DELETE (crash,
/// rede), o atendente volta a ser notificado sozinho depois de <see cref="TtlSeconds"/>.
/// </summary>
public sealed class TicketChatPresenceService : ITicketChatPresenceService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _viewers = new();
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public TicketChatPresenceService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public int TtlSeconds => (int)Ttl.TotalSeconds;

    public Task TouchAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        PruneExpired(now);
        _viewers[Key(userId, ticketId)] = now;
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default)
    {
        _viewers.TryRemove(Key(userId, ticketId), out _);
        return Task.CompletedTask;
    }

    public Task<bool> IsViewingAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default)
    {
        var key = Key(userId, ticketId);
        if (!_viewers.TryGetValue(key, out var seenAt))
            return Task.FromResult(false);

        if (_timeProvider.GetUtcNow() - seenAt > Ttl)
        {
            _viewers.TryRemove(key, out _);
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        if (now - _lastPrune < PruneInterval)
            return;

        _lastPrune = now;
        foreach (var (key, seenAt) in _viewers)
        {
            if (now - seenAt > Ttl)
                _viewers.TryRemove(key, out _);
        }
    }

    private static string Key(Guid userId, Guid ticketId) => userId.ToString("N") + ":" + ticketId.ToString("N");
}
