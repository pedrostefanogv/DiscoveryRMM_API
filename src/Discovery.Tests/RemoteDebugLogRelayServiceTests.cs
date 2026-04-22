using System.Reflection;
using Discovery.Api.Hubs;
using Discovery.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class RemoteDebugLogRelayServiceTests
{
    [Test]
    public async Task RelayAsync_ShouldNormalizeAndBroadcastLogPayload()
    {
        var manager = new RemoteDebugSessionManager();
        var agentId = Guid.NewGuid();
        var session = manager.StartSession(agentId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "info", 10);
        var proxy = new CaptureClientProxy();
        var relay = new RemoteDebugLogRelayService(
            manager,
            new TestRemoteDebugHubContext(proxy),
            NullLogger<RemoteDebugLogRelayService>.Instance);

        await relay.RelayAsync(
            new RemoteDebugInboundLogEntry(
                session.SessionId,
                agentId,
                "linha de teste   ",
                "WARN",
                new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc),
                null),
            RemoteDebugTransportNames.SignalR);

        Assert.That(proxy.MethodName, Is.EqualTo("RemoteDebugLog"));
        Assert.That(proxy.Arguments, Has.Count.EqualTo(1));

        var payload = proxy.Arguments[0]
            ?? throw new AssertionException("RemoteDebugLog payload should not be null.");
        Assert.That(GetProperty<Guid>(payload, "sessionId"), Is.EqualTo(session.SessionId));
        Assert.That(GetProperty<Guid>(payload, "agentId"), Is.EqualTo(agentId));
        Assert.That(GetProperty<string>(payload, "level"), Is.EqualTo("warn"));
        Assert.That(GetProperty<string>(payload, "message"), Is.EqualTo("linha de teste"));
        Assert.That(GetProperty<string>(payload, "transport"), Is.EqualTo(RemoteDebugTransportNames.SignalR));
        Assert.That(GetProperty<long>(payload, "sequence"), Is.EqualTo(1L));
    }

    [Test]
    public async Task RelayAsync_ShouldIgnoreLogsForWrongAgent()
    {
        var manager = new RemoteDebugSessionManager();
        var session = manager.StartSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "info", 10);
        var proxy = new CaptureClientProxy();
        var relay = new RemoteDebugLogRelayService(
            manager,
            new TestRemoteDebugHubContext(proxy),
            NullLogger<RemoteDebugLogRelayService>.Instance);

        await relay.RelayAsync(
            new RemoteDebugInboundLogEntry(
                session.SessionId,
                Guid.NewGuid(),
                "nao deve passar",
                "info",
                DateTime.UtcNow,
                null),
            RemoteDebugTransportNames.Nats);

        Assert.That(proxy.MethodName, Is.Null);
        Assert.That(proxy.Arguments, Is.Empty);
    }

    private static T GetProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            ?? throw new AssertionException($"Property '{name}' not found on payload type '{instance.GetType().Name}'.");

        return (T)(property.GetValue(instance)
            ?? throw new AssertionException($"Property '{name}' is null."));
    }

    private sealed class TestRemoteDebugHubContext : IHubContext<RemoteDebugHub>
    {
        public TestRemoteDebugHubContext(CaptureClientProxy proxy)
        {
            Clients = new TestHubClients(proxy);
            Groups = new NoopGroupManager();
        }

        public IHubClients Clients { get; }

        public IGroupManager Groups { get; }
    }

    private sealed class TestHubClients : IHubClients
    {
        private readonly IClientProxy _proxy;

        public TestHubClients(IClientProxy proxy)
        {
            _proxy = proxy;
        }

        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class CaptureClientProxy : IClientProxy
    {
        public string? MethodName { get; private set; }

        public List<object?> Arguments { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            MethodName = method;
            Arguments.Clear();
            Arguments.AddRange(args);
            return Task.CompletedTask;
        }
    }
}