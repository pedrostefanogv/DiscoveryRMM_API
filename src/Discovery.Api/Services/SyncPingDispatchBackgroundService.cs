using System.Collections.Concurrent;
using System.Threading.Channels;
using Discovery.Api.Hubs;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Discovery.Api.Services;

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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_pending.IsEmpty)
                {
                    var ping = await _queue.Reader.ReadAsync(stoppingToken);
                    EnqueuePending(ping);
                    DrainQueue();
                    continue;
                }

                var nextDueAtUtc = GetNextDueAtUtc();
                if (nextDueAtUtc is null)
                {
                    await WaitForIncomingPingAsync(stoppingToken);
                    continue;
                }

                var delay = nextDueAtUtc.Value - DateTime.UtcNow;
                if (delay <= TimeSpan.Zero)
                {
                    await FlushDuePingsAsync(stoppingToken);
                    continue;
                }

                var queueReadyTask = _queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var delayTask = Task.Delay(delay, stoppingToken);
                var completedTask = await Task.WhenAny(queueReadyTask, delayTask);

                if (completedTask == queueReadyTask && await queueReadyTask)
                {
                    DrainQueue();
                    continue;
                }

                await FlushDuePingsAsync(stoppingToken);
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
            EnqueuePending(ping);
        }
    }

    private void EnqueuePending(SyncInvalidationPingDto ping)
    {
        var key = BuildCoalesceKey(ping);
        _pending.AddOrUpdate(
            key,
            _ => new PendingPing(ping, DateTime.UtcNow.Add(DebounceWindow)),
            (_, __) => new PendingPing(ping, DateTime.UtcNow.Add(DebounceWindow)));
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

    private async Task WaitForIncomingPingAsync(CancellationToken cancellationToken)
    {
        if (await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            DrainQueue();
        }
    }

    private DateTime? GetNextDueAtUtc()
    {
        if (_pending.IsEmpty)
            return null;

        return _pending.MinBy(entry => entry.Value.DueAtUtc).Value.DueAtUtc;
    }

    private async Task DispatchPingAsync(SyncInvalidationPingDto ping, CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var messaging = scope.ServiceProvider.GetRequiredService<IAgentMessaging>();
        var deliveryRepo = scope.ServiceProvider.GetRequiredService<ISyncPingDeliveryRepository>();
        var outboundPing = SyncInvalidationPingMessage.FromDto(ping);
        var natsPublished = false;

        try
        {
            await deliveryRepo.CreateSentAsync(ping.EventId, ping.AgentId, ping.Resource, ping.Revision);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist sync ping delivery as Sent. AgentId={AgentId}, Resource={Resource}, Revision={Revision}",
                ping.AgentId,
                ping.Resource,
                ping.Revision);
        }

        if (messaging.IsConnected)
        {
            try
            {
                await messaging.PublishSyncPingAsync(ping.AgentId, outboundPing, cancellationToken);
                natsPublished = true;
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
        {
            if (natsPublished)
            {
                _logger.LogDebug(
                    "Sync ping delivered via NATS only (agent without SignalR session). AgentId={AgentId}, Resource={Resource}",
                    ping.AgentId,
                    ping.Resource);
            }

            _logger.LogDebug(
                "Skipping SignalR sync ping: agent not connected. AgentId={AgentId}, Resource={Resource}",
                ping.AgentId,
                ping.Resource);
            return;
        }

        var connectionId = AgentHub.GetConnectionId(ping.AgentId);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            _logger.LogDebug(
                "Skipping SignalR sync ping: connection id not found. AgentId={AgentId}, Resource={Resource}",
                ping.AgentId,
                ping.Resource);
            return;
        }

        try
        {
            await _agentHub.Clients.Client(connectionId)
                .SendAsync("SyncPing", outboundPing, cancellationToken);
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
