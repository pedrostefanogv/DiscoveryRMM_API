using System.Text.Json;
using Discovery.Core.Configuration;
using Discovery.Core.DTOs;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Discovery.Infrastructure.Services;

/// <summary>
/// Serviço de descoberta P2P por site com debounce e deduplicação por hash.
/// Monta snapshot de peers ativos e publica no subject NATS do site.
/// </summary>
public class P2pDiscoveryService : IDisposable
{
    private static readonly TimeSpan DefaultOnlineWindow = TimeSpan.FromMinutes(10);

    private readonly IP2pBootstrapRepository _bootstrapRepo;
    private readonly ISiteRepository _siteRepo;
    private readonly IAgentMessaging _messaging;
    private readonly P2pOptions _options;
    private readonly ILogger<P2pDiscoveryService> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, DebounceState> _debounceBySite = new();
    private long _globalSequence;

    public P2pDiscoveryService(
        IP2pBootstrapRepository bootstrapRepo,
        ISiteRepository siteRepo,
        IAgentMessaging messaging,
        IOptions<P2pOptions> options,
        ILogger<P2pDiscoveryService> logger)
    {
        _bootstrapRepo = bootstrapRepo;
        _siteRepo = siteRepo;
        _messaging = messaging;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Agenda publicação de descoberta para o site.
    /// Se outra mudança chegar dentro da janela de debounce, o timer reinicia.
    /// </summary>
    public void SchedulePublish(Guid siteId)
    {
        if (!_options.UseSiteSubject)
            return;

        lock (_lock)
        {
            if (_debounceBySite.TryGetValue(siteId, out var existing))
            {
                existing.Timer.Change(_options.PublishDebounceMs, Timeout.Infinite);
                return;
            }

            var timer = new Timer(
                _ => _ = PublishSnapshotAsync(siteId),
                null,
                _options.PublishDebounceMs,
                Timeout.Infinite);

            _debounceBySite[siteId] = new DebounceState(timer);
        }
    }

    /// <summary>
    /// Monta e publica o snapshot de peers do site.
    /// Se SkipPublishIfUnchanged estiver ativo, compara hash do snapshot anterior.
    /// </summary>
    public async Task PublishSnapshotAsync(Guid siteId)
    {
        try
        {
            var site = await _siteRepo.GetByIdAsync(siteId);
            if (site is null)
            {
                _logger.LogWarning("P2P discovery skipped: site {SiteId} not found.", siteId);
                return;
            }

            var onlineCutoff = DateTime.UtcNow - DefaultOnlineWindow;
            var peers = await _bootstrapRepo.GetSitePeersAsync(siteId, onlineCutoff, _options.MaxPeersPerSnapshot);

            var peerDtos = peers
                .Where(p => !string.IsNullOrWhiteSpace(p.PeerId))
                .Select(p => new P2pDiscoveryPeerDto(
                    p.AgentId,
                    p.PeerId,
                    DeserializeAddrs(p.AddrsJson),
                    p.Port,
                    p.LastHeartbeatAt))
                .ToList();

            var sequence = Interlocked.Increment(ref _globalSequence);
            var snapshot = new P2pDiscoverySnapshot(
                Version: 1,
                ClientId: site.ClientId,
                SiteId: siteId,
                GeneratedAtUtc: DateTime.UtcNow,
                TtlSeconds: _options.TtlSeconds,
                Sequence: sequence,
                Peers: peerDtos);

            var json = JsonSerializer.Serialize(snapshot, DiscoveryJsonOptions.Default);

            if (_options.SkipPublishIfUnchanged)
            {
                lock (_lock)
                {
                    if (_debounceBySite.TryGetValue(siteId, out var state))
                    {
                        var newHash = ComputeHash(json);
                        if (state.LastHash == newHash)
                        {
                            _logger.LogDebug("P2P discovery unchanged for site {SiteId}, skipping publish.", siteId);
                            return;
                        }
                        state.LastHash = newHash;
                    }
                }
            }

            await _messaging.PublishP2pDiscoverySnapshotAsync(site.ClientId, siteId, json);
            _logger.LogInformation("P2P discovery snapshot published for site {SiteId}: {PeerCount} peers, seq={Sequence}",
                siteId, peerDtos.Count, sequence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish P2P discovery snapshot for site {SiteId}.", siteId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var state in _debounceBySite.Values)
                state.Timer.Dispose();
            _debounceBySite.Clear();
        }
    }

    private static string ComputeHash(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static IReadOnlyList<string> DeserializeAddrs(string? addrsJson)
    {
        if (string.IsNullOrWhiteSpace(addrsJson)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(addrsJson) ?? new List<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private sealed record DebounceState(Timer Timer)
    {
        public string? LastHash { get; set; }
    }
}

/// <summary>
/// Opções de serialização JSON consistentes para mensagens P2P.
/// </summary>
internal static class DiscoveryJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
