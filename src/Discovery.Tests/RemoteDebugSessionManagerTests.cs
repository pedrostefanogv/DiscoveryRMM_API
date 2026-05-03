using Discovery.Api.Services;

namespace Discovery.Tests;

public class RemoteDebugSessionManagerTests
{
    [Test]
    public void StartSession_ShouldAllowOwnerAndAgentAccess()
    {
        var manager = new RemoteDebugSessionManager();
        var agentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        var session = manager.StartSession(agentId, userId, clientId, siteId, "debug", 10);

        Assert.That(session.SessionId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(manager.TryGetSessionForUser(session.SessionId, userId, out _), Is.True);
        Assert.That(manager.TryGetSessionForAgent(session.SessionId, agentId, out _), Is.True);
    }

    [Test]
    public void StartSession_ShouldSupersedePreviousSessionForAgent()
    {
        var manager = new RemoteDebugSessionManager();
        var agentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        var first = manager.StartSession(agentId, userId, clientId, siteId, "info", 10);
        var second = manager.StartSession(agentId, userId, clientId, siteId, "info", 10);

        Assert.That(first.SessionId, Is.Not.EqualTo(second.SessionId));
        Assert.That(manager.TryGetSession(first.SessionId, out _), Is.False);
        Assert.That(manager.TryGetSession(second.SessionId, out _), Is.True);
    }

    [Test]
    public void CloseSession_ShouldPreventFurtherAccess()
    {
        var manager = new RemoteDebugSessionManager();
        var agentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        var session = manager.StartSession(agentId, userId, clientId, siteId, "info", 10);
        var closed = manager.CloseSession(session.SessionId, "closed-by-test", userId);

        Assert.That(closed, Is.True);
        Assert.That(manager.TryGetSession(session.SessionId, out _), Is.False);
    }

    [Test]
    public void NextSequence_ShouldIncrementPerSession()
    {
        var manager = new RemoteDebugSessionManager();
        var agentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();

        var session = manager.StartSession(agentId, userId, clientId, siteId, "info", 10);

        var first = manager.NextSequence(session.SessionId);
        var second = manager.NextSequence(session.SessionId);

        Assert.That(first, Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(2));
    }

    [Test]
    public void StartSession_ShouldCreateCanonicalNatsSubject()
    {
        var manager = new RemoteDebugSessionManager();

        var session = manager.StartSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "info",
            10);

        Assert.That(session.NatsSubject, Does.StartWith("tenant."));
        Assert.That(session.NatsSubject, Does.EndWith("remote-debug.log"));
        Assert.That(session.PreferredTransport, Is.EqualTo(RemoteDebugTransportNames.Nats));
        Assert.That(session.FallbackTransport, Is.EqualTo(RemoteDebugTransportNames.SignalR));
        Assert.That(session.SignalRMethod, Is.EqualTo(RemoteDebugSignalRMethods.PushLog));
    }

    [Test]
    public void StartSession_WithSignalRPreferredTransport_ShouldSwapTransportOrder()
    {
        var manager = new RemoteDebugSessionManager();

        var session = manager.StartSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "info",
            10,
            RemoteDebugTransportNames.SignalR);

        Assert.That(session.PreferredTransport, Is.EqualTo(RemoteDebugTransportNames.SignalR));
        Assert.That(session.FallbackTransport, Is.EqualTo(RemoteDebugTransportNames.Nats));
    }
}
