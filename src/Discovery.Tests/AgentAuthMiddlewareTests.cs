using Discovery.Api.Middleware;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class AgentAuthMiddlewareTests
{
    private static AgentAuthMiddleware CreateMiddleware(RequestDelegate next)
        => new(next, new ConfigurationBuilder().Build(), NullLogger<AgentAuthMiddleware>.Instance);

    [Test]
    public async Task InvokeAsync_ShouldReturnUnauthorized_WhenAgentIdHeaderMissing()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");

        await middleware.InvokeAsync(context, authService, new FakeAgentRepository());

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(nextCalled, Is.False);
    }

    [Test]
    public async Task InvokeAsync_ShouldReturnBadRequest_WhenAgentIdHeaderInvalid()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", "not-a-guid");

        await middleware.InvokeAsync(context, authService, new FakeAgentRepository());

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task InvokeAsync_ShouldReturnUnauthorized_WhenAgentIdHeaderMismatch()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", Guid.NewGuid().ToString());

        await middleware.InvokeAsync(context, authService, new FakeAgentRepository());

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task InvokeAsync_ShouldSetContextAndCallNext_WhenAgentIdHeaderMatches()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", token.AgentId.ToString());

        await middleware.InvokeAsync(context, authService, new FakeAgentRepository());

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(nextCalled, Is.True);
        Assert.That(context.Items["AgentId"], Is.EqualTo(token.AgentId));
        Assert.That(context.Items["TokenId"], Is.EqualTo(token.Id));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Escopo do agente na LIXEIRA: o token continua válido (é o canal do
    // descomissionamento), mas o acesso REST fica restrito ao próprio
    // encerramento. Sem esta guarda, o token de um agente excluído abriria os
    // 68 endpoints de /agent-auth até o purge.
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task InvokeAsync_TrashedAgent_CanCallDecommissionEndpoint()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);
        var repo = new FakeAgentRepository { Agent = new Agent { Id = token.AgentId, DeletedAt = DateTime.UtcNow } };

        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", token.AgentId.ToString());

        await middleware.InvokeAsync(context, authService, repo);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(nextCalled, Is.True, "o DELETE /me é justamente o que conclui o descomissionamento");
    }

    [Test]
    public async Task InvokeAsync_TrashedAgent_IsForbiddenOnOtherEndpoints()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);
        var repo = new FakeAgentRepository { Agent = new Agent { Id = token.AgentId, DeletedAt = DateTime.UtcNow } };

        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", token.AgentId.ToString());

        await middleware.InvokeAsync(context, authService, repo);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        Assert.That(nextCalled, Is.False, "agente na lixeira não pode continuar reportando inventário");
    }

    [Test]
    public async Task InvokeAsync_ActiveAgent_KeepsFullAccess()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);
        var repo = new FakeAgentRepository { Agent = new Agent { Id = token.AgentId, DeletedAt = null } };

        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", token.AgentId.ToString());

        await middleware.InvokeAsync(context, authService, repo);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(nextCalled, Is.True);
    }

    [Test]
    public async Task InvokeAsync_AgentLookupFailure_DoesNotBlockActiveAgent()
    {
        // Fail-open: indisponibilidade do banco não pode derrubar o agente ativo.
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);
        var repo = new FakeAgentRepository { ThrowOnLookup = true };

        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", token.AgentId.ToString());

        await middleware.InvokeAsync(context, authService, repo);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(nextCalled, Is.True);
    }

    /// <summary>
    /// Stub mínimo: o middleware só usa GetByIdForDecommissionAsync (o lookup que
    /// enxerga a lixeira).
    /// </summary>
    private sealed class FakeAgentRepository : IAgentRepository
    {
        public Agent? Agent { get; init; }
        public bool ThrowOnLookup { get; init; }

        public Task<Agent?> GetByIdForDecommissionAsync(Guid id)
            => ThrowOnLookup
                ? throw new InvalidOperationException("banco indisponível (teste)")
                : Task.FromResult(Agent is not null && Agent.Id == id ? Agent : null);

        public Task<Agent?> GetByIdAsync(Guid id) => Task.FromResult(Agent);
        public Task<IReadOnlyList<Agent>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetAllAsync() => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId) => throw new NotSupportedException();
        public Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId) => throw new NotSupportedException();
        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, Discovery.Core.Enums.AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task SetPolicySyncAsync(Guid id, string fingerprint, DateTime syncedAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineIncludingTrashedAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task SetMaintenanceAsync(Guid id, bool enabled, string? reason, Guid changedByUserId) => throw new NotSupportedException();
        public Task TransferSiteAsync(Guid agentId, Guid newSiteId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> FindByFingerprintAsync(string fingerprintHash, Guid clientId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static DefaultHttpContext CreateAgentApiContext(string path, string rawToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Append("Authorization", $"Bearer {rawToken}");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FakeAgentAuthService : IAgentAuthService
    {
        private readonly AgentToken? _token;

        public FakeAgentAuthService(AgentToken? token)
        {
            _token = token;
        }

        public Task<(AgentToken Token, string RawToken)> CreateTokenAsync(Guid agentId, string? description)
            => Task.FromResult((_token ?? new AgentToken { AgentId = agentId }, "raw"));

        public Task<AgentToken?> ValidateTokenAsync(string rawToken)
            => Task.FromResult(_token);

        public Task RevokeTokenAsync(Guid tokenId) => Task.CompletedTask;

        public Task RevokeAllTokensAsync(Guid agentId) => Task.CompletedTask;

        public Task<IEnumerable<AgentToken>> GetTokensByAgentIdAsync(Guid agentId)
            => Task.FromResult<IEnumerable<AgentToken>>(_token is null ? [] : [_token]);

        public Task<bool> TryAcquireNatsSessionAsync(Guid tokenId, Guid agentId, string userNkey, TimeSpan sessionTtl)
            => Task.FromResult(true);

        public Task ReleaseNatsSessionAsync(Guid tokenId) => Task.CompletedTask;

        public Task UpdateLastNatsConnectedAsync(Guid tokenId) => Task.CompletedTask;
    }

}
