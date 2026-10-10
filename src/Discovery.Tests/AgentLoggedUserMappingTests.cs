using Discovery.Core.Entities;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Cqrs.Agents.QueryHandlers;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Precedência do usuário logado no AgentDto: valor ao vivo do heartbeat
/// (Redis) tem prioridade sobre o último valor persistido no banco.
/// </summary>
public class AgentLoggedUserMappingTests
{
    private static Agent BuildAgent(string? persisted) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Hostname = "HOST",
        LoggedUser = persisted,
    };

    [Test]
    public void MapToDto_PrefersLiveHeartbeatLoggedUser()
    {
        var dto = AgentQueryHelper.MapToDto(
            BuildAgent(@"DB\antigo"),
            new HeartbeatCacheEntry { LoggedUser = @"LIVE\atual" });

        Assert.That(dto.LoggedUser, Is.EqualTo(@"LIVE\atual"));
    }

    [Test]
    public void MapToDto_FallsBackToPersisted_WhenHeartbeatHasNoUser()
    {
        var dto = AgentQueryHelper.MapToDto(BuildAgent(@"DB\antigo"), new HeartbeatCacheEntry());

        Assert.That(dto.LoggedUser, Is.EqualTo(@"DB\antigo"));
    }

    [Test]
    public void MapToDto_FallsBackToPersisted_WhenOffline()
    {
        var dto = AgentQueryHelper.MapToDto(BuildAgent(@"DB\antigo"));

        Assert.That(dto.LoggedUser, Is.EqualTo(@"DB\antigo"));
    }

    [Test]
    public void WithLiveLoggedUser_EmptyStringMeansNoLiveSession_DoesNotFallBack()
    {
        // "" = agent novo sem sessão: a UI deve mostrar "—" e NÃO o último
        // usuário conhecido. Só null (agent não reporta) cai para o persistido.
        var agent = BuildAgent(@"DB\antigo");
        var dto = AgentQueryHelper.WithLiveLoggedUser(
            AgentQueryHelper.MapToDto(agent),
            agent,
            new HeartbeatCacheEntry { LoggedUser = "" });

        Assert.That(dto.LoggedUser, Is.Empty);
    }

    [Test]
    public void WithLiveLoggedUser_CarriesLiveLogonTime()
    {
        var since = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var agent = BuildAgent(null);

        var dto = AgentQueryHelper.WithLiveLoggedUser(
            AgentQueryHelper.MapToDto(agent),
            agent,
            new HeartbeatCacheEntry { LoggedUser = @"LIVE\atual", LoggedUserSince = since });

        Assert.That(dto.LoggedUser, Is.EqualTo(@"LIVE\atual"));
        Assert.That(dto.LoggedUserSince, Is.EqualTo(since));
    }
}
