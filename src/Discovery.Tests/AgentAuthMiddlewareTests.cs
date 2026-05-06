using Discovery.Api.Middleware;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Microsoft.AspNetCore.Http;

namespace Discovery.Tests;

public class AgentAuthMiddlewareTests
{
    [Test]
    public async Task InvokeAsync_ShouldReturnUnauthorized_WhenAgentIdHeaderMissing()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var nextCalled = false;
        var middleware = new AgentAuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");

        await middleware.InvokeAsync(context, authService);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(nextCalled, Is.False);
    }

    [Test]
    public async Task InvokeAsync_ShouldReturnBadRequest_WhenAgentIdHeaderInvalid()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var middleware = new AgentAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", "not-a-guid");

        await middleware.InvokeAsync(context, authService);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task InvokeAsync_ShouldReturnUnauthorized_WhenAgentIdHeaderMismatch()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var middleware = new AgentAuthMiddleware(_ => Task.CompletedTask);
        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", Guid.NewGuid().ToString());

        await middleware.InvokeAsync(context, authService);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
    }

    [Test]
    public async Task InvokeAsync_ShouldSetContextAndCallNext_WhenAgentIdHeaderMatches()
    {
        var token = new AgentToken { Id = Guid.NewGuid(), AgentId = Guid.NewGuid() };
        var authService = new FakeAgentAuthService(token);

        var nextCalled = false;
        var middleware = new AgentAuthMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateAgentApiContext("/api/v1/agent-auth/me/hardware", "mdz_valid");
        context.Request.Headers.Append("X-Agent-ID", token.AgentId.ToString());

        await middleware.InvokeAsync(context, authService);

        Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(nextCalled, Is.True);
        Assert.That(context.Items["AgentId"], Is.EqualTo(token.AgentId));
        Assert.That(context.Items["TokenId"], Is.EqualTo(token.Id));
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
    }

}
