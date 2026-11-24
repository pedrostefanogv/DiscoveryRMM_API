using Discovery.Core.Cqrs.AgentRegistration;
using Discovery.Core.Cqrs.Agents.Crud.Commands;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Cqrs.AgentRegistration;
using Discovery.Infrastructure.Cqrs.Agents.CommandHandlers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public sealed class AgentLifecycleDashboardEventTests
{
    [Test]
    public async Task RegisterAgentFromDeployToken_PublishesAgentRegisteredDashboardEvent()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var createdAgentId = Guid.NewGuid();
        var published = new List<DashboardEventMessage>();

        var deployTokenService = F02NullProxy<IDeployTokenService>.Create((method, _) =>
        {
            if (method.Name == nameof(IDeployTokenService.TryUseTokenAsync))
            {
                return Task.FromResult<DeployToken?>(new DeployToken
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    SiteId = siteId,
                    TokenPrefix = "dt_test",
                });
            }
            return null;
        });

        var agentRepo = F02NullProxy<IAgentRepository>.Create((method, args) =>
        {
            if (method.Name == nameof(IAgentRepository.CreateAsync) && args?[0] is Agent a)
            {
                a.Id = createdAgentId;
                return Task.FromResult(a);
            }
            return null;
        });

        var agentAuthService = F02NullProxy<IAgentAuthService>.Create((method, _) =>
        {
            if (method.Name == nameof(IAgentAuthService.CreateTokenAsync))
            {
                return Task.FromResult((new AgentToken { Id = Guid.NewGuid(), AgentId = createdAgentId }, "mdz_raw_token"));
            }
            return null;
        });

        var siteRepo = new F02SiteRepositoryStub(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        var configService = F02NullProxy<IConfigurationService>.Create();
        var redis = F02NullProxy<IRedisService>.Create();
        var messaging = F02NullProxy<IAgentMessaging>.Create((method, args) =>
        {
            if (method.Name == nameof(IAgentMessaging.PublishDashboardEventAsync) && args?[0] is DashboardEventMessage msg)
            {
                published.Add(msg);
                return Task.CompletedTask;
            }
            return null;
        });

        var handler = new RegisterAgentFromDeployTokenCommandHandler(
            deployTokenService,
            agentRepo,
            agentAuthService,
            siteRepo,
            configService,
            redis,
            NullLogger<RegisterAgentFromDeployTokenCommandHandler>.Instance,
            messaging);

        var result = await handler.Handle(
            new RegisterAgentFromDeployTokenCommand(
                DeployToken: "dt_secret",
                Hostname: "NOVO-PC",
                MacAddress: "00:11:22:33:44:55",
                Notes: null),
            CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(published, Has.Count.EqualTo(1));
        Assert.That(published[0].EventType, Is.EqualTo("AgentRegistered"));
        Assert.That(published[0].ClientId, Is.EqualTo(clientId));
        Assert.That(published[0].SiteId, Is.EqualTo(siteId));
    }

    [Test]
    public async Task ApproveZeroTouch_PublishesAgentApprovedDashboardEvent()
    {
        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var published = new List<DashboardEventMessage>();

        var agent = new Agent
        {
            Id = agentId,
            SiteId = siteId,
            Hostname = "PENDENTE-01",
            DisplayName = "PENDENTE-01",
            ZeroTouchPending = true,
        };

        var agentRepo = F02NullProxy<IAgentRepository>.Create((method, _) =>
        {
            if (method.Name == nameof(IAgentRepository.GetByIdAsync))
                return Task.FromResult<Agent?>(agent);
            return null;
        });

        var siteRepo = new F02SiteRepositoryStub(new Site { Id = siteId, ClientId = clientId, Name = "Matriz" });
        var scopeContext = F02ScopeContextStub.Global();
        var messaging = F02NullProxy<IAgentMessaging>.Create((method, args) =>
        {
            if (method.Name == nameof(IAgentMessaging.PublishDashboardEventAsync) && args?[0] is DashboardEventMessage msg)
            {
                published.Add(msg);
                return Task.CompletedTask;
            }
            return null;
        });

        var handler = new ApproveZeroTouchCommandHandler(agentRepo, siteRepo, scopeContext, messaging);
        var result = await handler.Handle(new ApproveZeroTouchCommand(agentId), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(published, Has.Count.EqualTo(1));
        Assert.That(published[0].EventType, Is.EqualTo("AgentApproved"));
        Assert.That(published[0].ClientId, Is.EqualTo(clientId));
        Assert.That(published[0].SiteId, Is.EqualTo(siteId));
    }
}
