using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Exclusão de agente dispara o comando de descomissionamento remoto (o agent
/// se desinstala da máquina) somente quando o agente está online. O envio é
/// best-effort: falha de dispatch não pode impedir a lixeira.
/// </summary>
[TestFixture]
public class AgentDeleteDecommissionTests
{
    private static readonly Guid SiteId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static DeleteAgentCommandHandler NewHandler(
        FakeAgentRepo repo,
        FakeAuthService auth,
        FakeDispatcher dispatcher)
        => new(repo, auth, dispatcher, new FakeRedis(), new FakeSiteRepo(), NullLogger<DeleteAgentCommandHandler>.Instance);

    [Test]
    public async Task Delete_OnlineAgent_DispatchesDecommissionAndStillTrashes()
    {
        var repo = new FakeAgentRepo(AgentStatus.Online);
        var auth = new FakeAuthService();
        var dispatcher = new FakeDispatcher();

        var result = await NewHandler(repo, auth, dispatcher)
            .Handle(new DeleteAgentCommand(repo.AgentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Dispatched, Has.Count.EqualTo(1));
        Assert.That(dispatcher.Dispatched[0].CommandType, Is.EqualTo(CommandType.DecommissionAgent));
        Assert.That(dispatcher.Dispatched[0].AgentId, Is.EqualTo(repo.AgentId));

        using var doc = JsonDocument.Parse(dispatcher.Dispatched[0].Payload);
        Assert.That(doc.RootElement.GetProperty("reason").GetString(), Is.EqualTo("trash"));

        Assert.That(auth.Revoked, Is.EqualTo(repo.AgentId));
        Assert.That(repo.Deleted, Is.EqualTo(repo.AgentId));
    }

    [Test]
    public async Task Delete_OfflineAgent_DoesNotDispatch()
    {
        var repo = new FakeAgentRepo(AgentStatus.Offline);
        var auth = new FakeAuthService();
        var dispatcher = new FakeDispatcher();

        var result = await NewHandler(repo, auth, dispatcher)
            .Handle(new DeleteAgentCommand(repo.AgentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(dispatcher.Dispatched, Is.Empty);
        Assert.That(repo.Deleted, Is.EqualTo(repo.AgentId));
    }

    [Test]
    public async Task Delete_DispatchFailure_StillTrashes()
    {
        var repo = new FakeAgentRepo(AgentStatus.Online);
        var auth = new FakeAuthService();
        var dispatcher = new FakeDispatcher { ThrowOnDispatch = true };

        var result = await NewHandler(repo, auth, dispatcher)
            .Handle(new DeleteAgentCommand(repo.AgentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(repo.Deleted, Is.EqualTo(repo.AgentId));
        Assert.That(auth.Revoked, Is.EqualTo(repo.AgentId));
    }

    [Test]
    public async Task Delete_UnknownAgent_ReturnsNotFound()
    {
        var repo = new FakeAgentRepo(AgentStatus.Online) { Missing = true };
        var dispatcher = new FakeDispatcher();

        var result = await NewHandler(repo, new FakeAuthService(), dispatcher)
            .Handle(new DeleteAgentCommand(repo.AgentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Errors[0].Code, Is.EqualTo("NotFound"));
        Assert.That(dispatcher.Dispatched, Is.Empty);
    }

    // -------------------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------------------

    private sealed class FakeAgentRepo(AgentStatus status) : IAgentRepository
    {
        public Guid AgentId { get; } = Guid.NewGuid();
        public Guid? Deleted { get; private set; }
        public bool Missing { get; init; }

        public Task<Agent?> GetByIdAsync(Guid id)
            => Task.FromResult<Agent?>(Missing
                ? null
                : new Agent { Id = AgentId, SiteId = SiteId, Hostname = "host", Status = status });

        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();

        public Task DeleteAsync(Guid id)
        {
            Deleted = id;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
    }

    private sealed class FakeAuthService : IAgentAuthService
    {
        public Guid? Revoked { get; private set; }

        public Task RevokeAllTokensAsync(Guid agentId)
        {
            Revoked = agentId;
            return Task.CompletedTask;
        }

        public Task<(AgentToken Token, string RawToken)> CreateTokenAsync(Guid agentId, string? description) => throw new NotSupportedException();
        public Task<AgentToken?> ValidateTokenAsync(string rawToken) => throw new NotSupportedException();
        public Task RevokeTokenAsync(Guid tokenId) => throw new NotSupportedException();
        public Task<IEnumerable<AgentToken>> GetTokensByAgentIdAsync(Guid agentId) => throw new NotSupportedException();
        public Task<bool> TryAcquireNatsSessionAsync(Guid tokenId, Guid agentId, string userNkey, TimeSpan sessionTtl) => throw new NotSupportedException();
        public Task ReleaseNatsSessionAsync(Guid tokenId) => throw new NotSupportedException();
        public Task UpdateLastNatsConnectedAsync(Guid tokenId) => throw new NotSupportedException();
    }

    private sealed class FakeDispatcher : IAgentCommandDispatcher
    {
        public List<AgentCommand> Dispatched { get; } = [];
        public bool ThrowOnDispatch { get; init; }

        public Task<AgentCommand> DispatchAsync(AgentCommand command, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDispatch)
                throw new InvalidOperationException("NATS indisponível (teste)");

            command.Id = Guid.NewGuid();
            Dispatched.Add(command);
            return Task.FromResult(command);
        }
    }

    private sealed class FakeRedis : IRedisService
    {
        public bool IsConnected => true;
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
        public Task<long> IncrementAsync(string key) => Task.FromResult(0L);
        public Task<long> IncrementByAsync(string key, long amount) => Task.FromResult(amount);
        public Task SetAsync(string key, string value, int expirySeconds = 3600) => Task.CompletedTask;
        public Task<bool> SetExpiryAsync(string key, int expirySeconds) => Task.FromResult(true);
        public Task<int> GetTtlSecondsAsync(string key) => Task.FromResult(0);
        public Task DeleteAsync(string key) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix) => Task.CompletedTask;
        public Task PublishAsync(string channel, string message) => Task.CompletedTask;
        public Task SubscribeAsync(string channel, Action<string, string> handler) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetKeysByPrefixAsync(string prefix, int maxResults = 10000) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<bool> SetIfNotExistsAsync(string key, string value, int expirySeconds) => Task.FromResult(true);
    }

    private sealed class FakeSiteRepo : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id)
            => Task.FromResult<Site?>(new Site { Id = id, ClientId = Guid.NewGuid(), Name = "site" });

        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false) => throw new NotSupportedException();
    }
}
