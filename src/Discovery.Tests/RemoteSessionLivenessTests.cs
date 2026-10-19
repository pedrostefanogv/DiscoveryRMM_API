using Discovery.Api.Services;
using Discovery.Core.Configuration;
using Discovery.Core.Enums;
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

    [Test]
    public async Task ApplyConsentDecision_Denied_NotificaAgenteStop()
    {
        // Regressão: negar no prompt precisa avisar o agente para encerrar o
        // worker/prompt — sem isso o serviço local podia relançar o worker e o
        // prompt reaparecia (e os reportes seguintes davam 409).
        var session = ActiveSession(DateTime.UtcNow);
        session.Status = RemoteSessionStatus.PendingConsent;
        var dispatcher = new CapturingDispatcher();
        var manager = CreateManager(session, ttlMinutes: 30, maxDurationMinutes: 120, dispatcher: dispatcher);

        var updated = await manager.ApplyConsentDecisionAsync(
            session.Id, accepted: false, outcome: "denied", reason: "user_denied", promptBackend: "native");

        Assert.That(updated.Status, Is.EqualTo(RemoteSessionStatus.Denied));
        Assert.That(dispatcher.Commands, Has.Count.EqualTo(1));
        Assert.That(dispatcher.Commands[0].CommandType, Is.EqualTo(CommandType.RemoteSessionStop));
        Assert.That(dispatcher.Commands[0].AgentId, Is.EqualTo(session.AgentId));
        Assert.That(dispatcher.Commands[0].Payload, Does.Contain("\"action\":\"stop\""));
    }

    [Test]
    public async Task ApplyConsentDecision_Accepted_NaoEnviaStop()
    {
        var session = ActiveSession(DateTime.UtcNow);
        session.Status = RemoteSessionStatus.PendingConsent;
        var dispatcher = new CapturingDispatcher();
        var manager = CreateManager(session, ttlMinutes: 30, maxDurationMinutes: 120, dispatcher: dispatcher);

        var updated = await manager.ApplyConsentDecisionAsync(
            session.Id, accepted: true, outcome: "granted", reason: "user_allowed", promptBackend: "native");

        Assert.That(updated.Status, Is.EqualTo(RemoteSessionStatus.Active));
        Assert.That(dispatcher.Commands, Is.Empty);
    }

    [Test]
    public async Task ApplyConsentDecision_StoppedByServer_NaoDuplicaStop()
    {
        // O cancelamento do console ("Cancelar solicitação") já despacha o stop
        // no StopRemoteSessionCommandHandler; o manager NÃO deve duplicar.
        var session = ActiveSession(DateTime.UtcNow);
        session.Status = RemoteSessionStatus.PendingConsent;
        var dispatcher = new CapturingDispatcher();
        var manager = CreateManager(session, ttlMinutes: 30, maxDurationMinutes: 120, dispatcher: dispatcher);

        var updated = await manager.ApplyConsentDecisionAsync(
            session.Id, accepted: false, outcome: "stopped-by-server", reason: "stopped_by_operator", promptBackend: null);

        Assert.That(updated.Status, Is.EqualTo(RemoteSessionStatus.Denied));
        Assert.That(dispatcher.Commands, Is.Empty);
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

    private static RemoteSessionManager CreateManager(
        RemoteSession session,
        int ttlMinutes,
        int maxDurationMinutes,
        IAgentCommandDispatcher? dispatcher = null)
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
            NullLogger<RemoteSessionManager>.Instance,
            dispatcher ?? new NoopCommandDispatcher());
    }

    private sealed class CapturingDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Commands { get; } = new();

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(command);
        }
    }

    private sealed class NoopCommandDispatcher : IAgentCommandDispatcher
    {
        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(command);
    }

    private sealed class FakeSessionRepository(RemoteSession seed) : IRemoteSessionRepository
    {
        private readonly Dictionary<Guid, RemoteSession> _sessions = new() { [seed.Id] = seed };

        public Task<RemoteSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_sessions.TryGetValue(id, out var s) ? s : null);

        private static bool IsOpen(RemoteSession session) =>
            session.Status is "active" or "pending_consent";

        public Task<IEnumerable<RemoteSession>> GetActiveByAgentAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSession>>(
                _sessions.Values.Where(s => s.AgentId == agentId && IsOpen(s)).ToList());

        public Task<IEnumerable<RemoteSession>> GetActiveByUserAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSession>>(
                _sessions.Values.Where(s => s.UserId == userId && IsOpen(s)).ToList());

        public Task<int> CountActiveByAgentAsync(Guid agentId, CancellationToken ct = default)
            => Task.FromResult(_sessions.Values.Count(s => s.AgentId == agentId && IsOpen(s)));

        public Task<int> CountActiveByUserAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(_sessions.Values.Count(s => s.UserId == userId && IsOpen(s)));

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

        public Task<IEnumerable<RemoteSession>> GetPendingConsentPastDeadlineAsync(DateTime before, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSession>>(
                _sessions.Values.Where(s => s.Status == "pending_consent"
                    && s.ConsentDeadlineAt.HasValue
                    && s.ConsentDeadlineAt.Value < before).ToList());

        public Task<IEnumerable<RemoteSession>> GetNeverActivatedActiveSessionsAsync(DateTime startedBefore, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSession>>(
                _sessions.Values.Where(s => s.Status == "active"
                    && s.StartedAt < startedBefore
                    && s.LastActivityAt <= s.StartedAt).ToList());

        public Task TouchActivityAsync(Guid id, DateTime at, CancellationToken ct = default)
        {
            if (_sessions.TryGetValue(id, out var s)) s.LastActivityAt = at;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAuditRepository : IRemoteSessionAuditRepository
    {
        public Task AddAsync(RemoteSessionAudit audit, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEnumerable<RemoteSessionAudit>> GetBySessionAsync(Guid sessionId, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<RemoteSessionAudit>>([]);
    }
}
