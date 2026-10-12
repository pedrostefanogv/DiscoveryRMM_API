using System.Collections.Concurrent;
using Discovery.Core.Interfaces;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Presenca em memoria do chat de suporte (janela aberta, visivel e em foco).
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
    private const int MaxClientIdLength = 64;

    private readonly ConcurrentDictionary<string, PresenceEntry> _viewers = new();
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public TicketChatPresenceService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public int TtlSeconds => (int)Ttl.TotalSeconds;

    public Task TouchAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        PruneExpired(now);
        _viewers[Key(userId, ticketId)] = new PresenceEntry(NormalizeClientId(clientId), now);
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid userId, Guid ticketId, string? clientId = null, CancellationToken cancellationToken = default)
    {
        var key = Key(userId, ticketId);
        var owner = NormalizeClientId(clientId);

        if (owner.Length == 0)
        {
            _viewers.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        // So o dono ATUAL pode limpar. Sem isso, o DELETE de uma montagem
        // antiga (StrictMode) ou de uma janela ja substituida chega depois do
        // POST novo e apaga a presenca valida — a supressao falharia por corrida.
        if (_viewers.TryGetValue(key, out var entry) && entry.ClientId == owner)
        {
            _viewers.TryRemove(new KeyValuePair<string, PresenceEntry>(key, entry));
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsViewingAsync(Guid userId, Guid ticketId, CancellationToken cancellationToken = default)
    {
        var key = Key(userId, ticketId);
        if (!_viewers.TryGetValue(key, out var entry))
            return Task.FromResult(false);

        if (_timeProvider.GetUtcNow() - entry.SeenAt > Ttl)
        {
            _viewers.TryRemove(new KeyValuePair<string, PresenceEntry>(key, entry));
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        if (now - _lastPrune < PruneInterval)
            return;

        _lastPrune = now;
        foreach (var (key, entry) in _viewers)
        {
            if (now - entry.SeenAt > Ttl)
                _viewers.TryRemove(new KeyValuePair<string, PresenceEntry>(key, entry));
        }
    }

    private static string Key(Guid userId, Guid ticketId) => userId.ToString("N") + ":" + ticketId.ToString("N");

    /// <summary>Normaliza o identificador de janela (trim + teto) vindo da query string.</summary>
    private static string NormalizeClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return string.Empty;

        var trimmed = clientId.Trim();
        return trimmed.Length <= MaxClientIdLength ? trimmed : trimmed[..MaxClientIdLength];
    }

    private readonly record struct PresenceEntry(string ClientId, DateTimeOffset SeenAt);
}