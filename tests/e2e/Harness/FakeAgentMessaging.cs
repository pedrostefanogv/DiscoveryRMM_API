using System.Collections.Concurrent;
using Discovery.Core.DTOs;
using Discovery.Core.Interfaces;

namespace Discovery.E2E.Tests.Harness;

public sealed record RecordedAgentCommand(Guid? ClientId, Guid? SiteId, Guid AgentId, Guid CommandId, string CommandType, string Payload);
public sealed record RecordedFanout(string Scope, Guid? ClientId, Guid? SiteId, CommandDispatchEnvelope Envelope);

/// <summary>
/// Thread-safe in-memory IAgentMessaging recording dispatched commands, fanouts, and events.
/// Used by the HTTP host when NATS broker is decoupled; F03/F04 tests drive real NatsAgentMessaging
/// against FakeNatsTcpServer.
/// </summary>
public sealed class FakeAgentMessaging : IAgentMessaging
{
    public ConcurrentQueue<RecordedAgentCommand> SentCommands { get; } = new();
    public ConcurrentQueue<RecordedFanout> FanoutCommands { get; } = new();
    public ConcurrentQueue<DashboardEventMessage> DashboardEvents { get; } = new();
    public ConcurrentQueue<(Guid AgentId, SyncInvalidationPingMessage Ping, Guid? ClientId, Guid? SiteId)> SyncPings { get; } = new();
    public ConcurrentQueue<(Guid ClientId, Guid SiteId, Guid AgentId, string Payload)> RemoteDebugControls { get; } = new();
    public ConcurrentQueue<(Guid ClientId, Guid SiteId, Guid AgentId, TicketChatEnvelope Envelope)> SupportChats { get; } = new();

    public bool IsConnected => true;

    public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload)
    {
        SentCommands.Enqueue(new RecordedAgentCommand(null, null, agentId, commandId, commandType, payload));
        return Task.CompletedTask;
    }

    public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        FanoutCommands.Enqueue(new RecordedFanout("site", clientId, siteId, envelope));
        return Task.CompletedTask;
    }

    public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        FanoutCommands.Enqueue(new RecordedFanout("client", clientId, null, envelope));
        return Task.CompletedTask;
    }

    public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        FanoutCommands.Enqueue(new RecordedFanout("global", null, null, envelope));
        return Task.CompletedTask;
    }

    public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default)
    {
        DashboardEvents.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default)
    {
        SyncPings.Enqueue((agentId, ping, null, null));
        return Task.CompletedTask;
    }

    public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, Guid overrideClientId, Guid overrideSiteId, CancellationToken cancellationToken = default)
    {
        SyncPings.Enqueue((agentId, ping, overrideClientId, overrideSiteId));
        return Task.CompletedTask;
    }

    public Task SendCommandToSubjectAsync(Guid clientId, Guid siteId, Guid agentId, Guid commandId, string commandType, string payload)
    {
        SentCommands.Enqueue(new RecordedAgentCommand(clientId, siteId, agentId, commandId, commandType, payload));
        return Task.CompletedTask;
    }

    public Task PublishRemoteDebugControlAsync(Guid clientId, Guid siteId, Guid agentId, string payload, CancellationToken cancellationToken = default)
    {
        RemoteDebugControls.Enqueue((clientId, siteId, agentId, payload));
        return Task.CompletedTask;
    }

    public Task PublishSupportChatServerAsync(Guid clientId, Guid siteId, Guid agentId, TicketChatEnvelope envelope, CancellationToken cancellationToken = default)
    {
        SupportChats.Enqueue((clientId, siteId, agentId, envelope));
        return Task.CompletedTask;
    }

    public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Clear()
    {
        SentCommands.Clear();
        FanoutCommands.Clear();
        DashboardEvents.Clear();
        SyncPings.Clear();
        RemoteDebugControls.Clear();
        SupportChats.Clear();
    }
}
