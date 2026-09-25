using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public class RemoteDebugSessionManagerTests
{
    private static RemoteDebugSessionManager CreateManager(RemoteDebugOptions? options = null)
        => new(Options.Create(options ?? new RemoteDebugOptions()));

    [Test]
    public void StartSession_ShouldAllowOwnerAndAgentAccess()
    {
        var manager = CreateManager();
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
        var manager = CreateManager();
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
        var manager = CreateManager();
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
        var manager = CreateManager();
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
        var manager = CreateManager();

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
    }

    [Test]
    public void StartSession_WithNatsWsPreferredTransport_ShouldUseNatsWs()
    {
        var manager = CreateManager();

        var session = manager.StartSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "info",
            10,
            RemoteDebugTransportNames.NatsWs);

        Assert.That(session.PreferredTransport, Is.EqualTo(RemoteDebugTransportNames.NatsWs));
    }

    [Test]
    public void RenewSession_ShouldExtendWithinCap()
    {
        var manager = CreateManager();
        var userId = Guid.NewGuid();
        var session = manager.StartSession(Guid.NewGuid(), userId, Guid.NewGuid(), Guid.NewGuid(), "info", 5);

        var before = session.ExpiresAtUtc;
        var renewed = manager.TryRenewSession(session.SessionId, userId, out var updated);

        Assert.That(renewed, Is.True);
        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.ExpiresAtUtc, Is.GreaterThan(before));
        Assert.That(updated.ExpiresAtUtc, Is.LessThanOrEqualTo(updated.MaxExpiresAtUtc));
    }

    [Test]
    public void RenewSession_AfterMaxDuration_ShouldCloseWithMaxDuration()
    {
        var manager = CreateManager(new RemoteDebugOptions { MaxSessionDurationMinutes = 60 });
        var userId = Guid.NewGuid();
        var session = manager.StartSession(Guid.NewGuid(), userId, Guid.NewGuid(), Guid.NewGuid(), "info", 5);

        // Simula o teto atingido sem esperar 1h.
        session.StartedAtUtc = DateTime.UtcNow.AddMinutes(-120);
        session.MaxExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);

        var renewed = manager.TryRenewSession(session.SessionId, userId, out _);

        Assert.That(renewed, Is.False);
        Assert.That(manager.TryGetSession(session.SessionId, out _), Is.False);
        Assert.That(session.EndReason, Is.EqualTo("max-duration"));
    }

    [Test]
    public void CleanupExpiredSessions_ShouldCloseWhenKeepAliveStops()
    {
        var manager = CreateManager(new RemoteDebugOptions { KeepAliveTimeoutSeconds = 1 });
        var session = manager.StartSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "info", 30);

        // Viewer morreu sem renovar: o keepalive parou ha muito tempo.
        session.LastKeepAliveAtUtc = DateTime.UtcNow.AddSeconds(-30);

        var cleaned = manager.CleanupExpiredSessions();

        Assert.That(cleaned, Is.GreaterThanOrEqualTo(1));
        Assert.That(session.EndReason, Is.EqualTo("keepalive-timeout"));
        Assert.That(manager.TryGetSession(session.SessionId, out _), Is.False);
    }

    [Test]
    public void TrySetLogLevel_ShouldApplyWithoutReplacingSession()
    {
        var manager = CreateManager();
        var userId = Guid.NewGuid();
        var session = manager.StartSession(Guid.NewGuid(), userId, Guid.NewGuid(), Guid.NewGuid(), "info", 10);
        var originalId = session.SessionId;

        var applied = manager.TrySetLogLevel(session.SessionId, userId, "warn", out var updated);

        Assert.That(applied, Is.True);
        Assert.That(updated!.LogLevel, Is.EqualTo("warn"));
        Assert.That(updated.SessionId, Is.EqualTo(originalId), "a troca de nivel nao deve trocar a sessao");
    }

    [Test]
    public void TryRenewSession_WithWrongUser_ShouldBeRejected()
    {
        var manager = CreateManager();
        var session = manager.StartSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "info", 10);

        var renewed = manager.TryRenewSession(session.SessionId, Guid.NewGuid(), out _);

        Assert.That(renewed, Is.False);
    }
}
