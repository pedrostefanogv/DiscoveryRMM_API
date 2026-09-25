using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Entities;
using Discovery.Core.Helpers;
using Discovery.Core.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Discovery.Tests;

public class RemoteSessionLivenessTests
{
    [Test]
    public void TryClampRenewal_ExtendsWithinMax()
    {
        var now = new DateTime(2026, 3, 28, 10, 30, 0, DateTimeKind.Utc);
        var started = now.AddMinutes(-10);

        var ok = SessionLivenessPolicy.TryClampRenewal(now, started, 30, 120, out var next);

        Assert.That(ok, Is.True);
        Assert.That(next, Is.EqualTo(now.AddMinutes(30)));
    }

    [Test]
    public void TryClampRenewal_ClampsAtMaxDuration()
    {
        var started = new DateTime(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc);
        var now = started.AddMinutes(100);

        var ok = SessionLivenessPolicy.TryClampRenewal(now, started, 30, 120, out var next);

        Assert.That(ok, Is.True);
        Assert.That(next, Is.EqualTo(started.AddMinutes(120)));
    }

    [Test]
    public void TryClampRenewal_FailsAfterMaxDuration()
    {
        var started = new DateTime(2026, 3, 28, 10, 0, 0, DateTimeKind.Utc);
        var now = started.AddMinutes(121);

        var ok = SessionLivenessPolicy.TryClampRenewal(now, started, 30, 120, out var next);

        Assert.That(ok, Is.False);
        Assert.That(next, Is.EqualTo(started.AddMinutes(120)));
    }

    [Test]
    public void RemoteAccessOptions_LivenessDefaults()
    {
        var liveness = new RemoteAccessOptions().Liveness;

        Assert.That(liveness.PingIntervalSeconds, Is.EqualTo(5));
        Assert.That(liveness.MissedPingsBeforeClose, Is.EqualTo(3));
        Assert.That(liveness.InitialGraceSeconds, Is.EqualTo(60));
        Assert.That(liveness.KeepAliveTimeoutSeconds, Is.EqualTo(300));
        Assert.That(liveness.SweepIntervalSeconds, Is.EqualTo(15));
    }

    [Test]
    public async Task RenewSessionAsync_ClampsAndRecordsActivity()
    {
        var session = ActiveSession(DateTime.UtcNow.AddMinutes(-10));
        var manager = CreateManager(session, ttlMinutes: 30, maxDurationMinutes: 120);

        var updated = await manager.RenewSessionAsync(session.Id, session.UserId);

        Assert.That(updated.Status, Is.EqualTo("active"));
        Assert.That(updated.LastActivityAt, Is.Not.Null);
        Assert.That(updated.ExpiresAt, Is.GreaterThan(DateTime.UtcNow.AddMinutes(25)));
    }

    [Test]
    public async Task RenewSessionAsync_ExpiresWhenMaxDurationReached()
    {
        // Started ha 200 min com teto de 120 min: o renew nao estende e fecha.
        var session = ActiveSession(DateTime.UtcNow.AddMinutes(-200));
        var manager = CreateManager(session, ttlMinutes: 30, maxDurationMinutes: 120);

        var updated = await manager.RenewSessionAsync(session.Id, session.UserId);

        Assert.That(updated.Status, Is.EqualTo("expired"));
        Assert.That(updated.ClosedAt, Is.Not.Null);
        Assert.That(updated.ExpiresAt, Is.EqualTo(session.StartedAt.AddMinutes(120)));
    }

    [Test]
    public async Task RenewSessionAsync_ReturnsClosedSessionWithoutThrowing()
    {
        var session = ActiveSession(DateTime.UtcNow.AddMinutes(-5));
        session.Status = "closed";
        var manager = CreateManager(session, ttlMinutes: 30, maxDurationMinutes: 120);

        var updated = await manager.RenewSessionAsync(session.Id, session.UserId);

        Assert.That(updated.Status, Is.EqualTo("closed"));
    }

    private static RemoteSession ActiveSession(DateTime startedAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        Status = "active",
        StartedAt = startedAt,
        ExpiresAt = startedAt.AddMinutes(1),
    };

    private static RemoteSessionManager CreateManager(RemoteSession session, int ttlMinutes, int maxDurationMinutes)
    {
        var options = Options.Create(new RemoteAccessOptions
        {
            DefaultTtlMinutes = ttlMinutes,
            MaxSessionDurationMinutes = maxDurationMinutes,
        });

        return new RemoteSessionManager(
            new FakeSessionRepository(session),
            new FakeAuditRepository(),
            options,
            NullLogger<RemoteSessionManager>.Instance);
    }

    private sealed class FakeSessionRepository(RemoteSession seed) : IRemoteSessionRepository
    {
        private readonly Dictionary<Guid, RemoteSession> _sessions = new() { [seed.Id] = seed };

        public Task<RemoteSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_sessions.TryGetValue(id, out var s) ? s : null);

        public Task<IEnumerable<RemoteSession>> GetActiveByAgentAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSession>>(
                _sessions.Values.Where(s => s.AgentId == agentId && s.Status == "active").ToList());

        public Task<IEnumerable<RemoteSession>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSession>>(
                _sessions.Values.Where(s => s.UserId == userId && s.Status == "active").ToList());

        public Task<int> CountActiveByAgentAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult(_sessions.Values.Count(s => s.AgentId == agentId && s.Status == "active"));

        public Task<int> CountActiveByUserAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(_sessions.Values.Count(s => s.UserId == userId && s.Status == "active"));

        public Task<RemoteSession> CreateAsync(RemoteSession session, CancellationToken ct = default)
        {
            _sessions[session.Id] = session;
            return Task.FromResult(session);
        }

        public Task<RemoteSession> UpdateAsync(RemoteSession session, CancellationToken ct = default)
        {
            _sessions[session.Id] = session;
            return Task.FromResult(session);
        }

        public Task<IEnumerable<RemoteSession>> GetExpiredAsync(DateTime before, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSession>>(
                _sessions.Values.Where(s => s.Status == "active" && s.ExpiresAt < before).ToList());
    }

    private sealed class FakeAuditRepository : IRemoteSessionAuditRepository
    {
        public Task AddAsync(RemoteSessionAudit audit, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEnumerable<RemoteSessionAudit>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSessionAudit>>([]);
    }
}
