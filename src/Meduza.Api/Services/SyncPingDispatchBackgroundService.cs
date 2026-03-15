using System.Collections.Concurrent;
using System.Threading.Channels;
using Meduza.Api.Hubs;
using Meduza.Core.DTOs;
using Meduza.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Meduza.Api.Services;

public class SyncPingDispatchBackgroundService : BackgroundService, ISyncPingDispatchQueue
{
    private readonly Channel<SyncInvalidationPingDto> _queue = Channel.CreateUnbounded<SyncInvalidationPingDto>();
    private readonly ConcurrentDictionary<string, PendingPing> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<AgentHub> _agentHub;
    private readonly ILogger<SyncPingDispatchBackgroundService> _logger;
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(1200);

    public SyncPingDispatchBackgroundService(
        IServiceProvider serviceProvider,
        IHubContext<AgentHub> agentHub,
        ILogger<SyncPingDispatchBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _agentHub = agentHub;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(SyncInvalidationPingDto ping, CancellationToken cancellationToken = default)
    {
        return _queue.Writer.WriteAsync(ping, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DrainQueue();
                await FlushDuePingsAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in sync ping dispatch background service.");
            }
        }

        DrainQueue();
        await FlushAllPendingAsync(stoppingToken);
    }

    private void DrainQueue()
    {
        while (_queue.Reader.TryRead(out var ping))
        {
            var key = BuildCoalesceKey(ping);
            _pending.AddOrUpdate(
                key,
                _ => new PendingPing(ping, DateTime.UtcNow.Add(DebounceWindow)),
                (_, __) => new PendingPing(ping, DateTime.UtcNow.Add(DebounceWindow)));
        }
    }

    private async Task FlushDuePingsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var dueKeys = _pending
            .Where(x => x.Value.DueAtUtc <= now)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in dueKeys)
        {
            if (!_pending.TryRemove(key, out var pending))
                continue;

            await DispatchPingAsync(pending.Ping, cancellationToken);
        }
    }

    private async Task FlushAllPendingAsync(CancellationToken cancellationToken)
    {
        var keys = _pending.Keys.ToList();
        foreach (var key in keys)
        {
            if (!_pending.TryRemove(key, out var pending))
                continue;

            await DispatchPingAsync(pending.Ping, cancellationToken);
        }
    }

    private async Task DispatchPingAsync(SyncInvalidationPingDto ping, CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();

        if (messaging.IsConnected)
        {
            try
            {
                await messaging.PublishSyncPingAsync(ping.AgentId, ping, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "NATS publish failed for sync ping. Falling back to SignalR. AgentId={AgentId}, Resource={Resource}",
                    ping.AgentId,
                    ping.Resource);
            }
        }

        if (!AgentHub.IsAgentConnected(ping.AgentId))
            return;

        try
        {
            await _agentHub.Clients.Group($"agent-{ping.AgentId}")
                .SendAsync("SyncPing", ping, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SignalR fallback publish failed for sync ping. AgentId={AgentId}, Resource={Resource}",
                ping.AgentId,
                ping.Resource);
        }
    }

    private static string BuildCoalesceKey(SyncInvalidationPingDto ping)
    {
        return $"{ping.AgentId:N}:{ping.Resource}:{ping.InstallationType?.ToString() ?? "all"}";
    }

    private readonly record struct PendingPing(SyncInvalidationPingDto Ping, DateTime DueAtUtc);
}
