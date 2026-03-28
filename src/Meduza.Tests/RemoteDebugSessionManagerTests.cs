using Meduza.Api.Services;

namespace Meduza.Tests;

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

        var session = manager.StartSession(agentId, userId, clientId, siteId, "debug", "signalr", 10);

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

        var first = manager.StartSession(agentId, userId, clientId, siteId, "info", "signalr", 10);
        var second = manager.StartSession(agentId, userId, clientId, siteId, "info", "signalr", 10);

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

        var session = manager.StartSession(agentId, userId, clientId, siteId, "info", "signalr", 10);
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

        var session = manager.StartSession(agentId, userId, clientId, siteId, "info", "signalr", 10);

        var first = manager.NextSequence(session.SessionId);
        var second = manager.NextSequence(session.SessionId);

        Assert.That(first, Is.EqualTo(1));
        Assert.That(second, Is.EqualTo(2));
    }
}
