using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Discovery.Core.Cqrs.Sites.Queries;
using Discovery.Core.Entities;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Cqrs.Sites;
using NUnit.Framework;

namespace Discovery.Tests;

[TestFixture]
public class SitesQueryHandlerTests
{
    private static Site MakeSite(Guid clientId, string name, bool active = true)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Name = name,
            IsActive = active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    [Test]
    public async Task GetAllSites_Global_ReturnsAllIncludingInactiveWhenRequested()
    {
        var client = Guid.NewGuid();
        var active = MakeSite(client, "A");
        var inactive = MakeSite(client, "B", active: false);
        var handler = new GetAllSitesQueryHandler(
            new FakeSiteRepo([active, inactive]),
            new FakeClientRepo(),
            new FakeScope(new UserScopeAccess { HasGlobalAccess = true }));

        var result = await handler.Handle(new GetAllSitesQuery(IncludeInactive: true), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!.Select(site => site.Id), Is.EquivalentTo(new[] { active.Id, inactive.Id }));
    }

    [Test]
    public async Task GetAllSites_SiteScoped_ReturnsOnlyAllowedSites()
    {
        var client = Guid.NewGuid();
        var allowed = MakeSite(client, "Allowed");
        var other = MakeSite(client, "Other");
        var handler = new GetAllSitesQueryHandler(
            new FakeSiteRepo([allowed, other]),
            new FakeClientRepo(),
            new FakeScope(new UserScopeAccess { AllowedSiteIds = [allowed.Id] }));

        var result = await handler.Handle(new GetAllSitesQuery(), CancellationToken.None);

        Assert.That(result.Value!.Select(site => site.Id), Is.EquivalentTo(new[] { allowed.Id }));
    }

    [Test]
    public async Task GetAllSites_ClientScoped_ReturnsClientSites()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var siteA = MakeSite(clientA, "A");
        var siteB = MakeSite(clientB, "B");
        var handler = new GetAllSitesQueryHandler(
            new FakeSiteRepo([siteA, siteB]),
            new FakeClientRepo(),
            new FakeScope(new UserScopeAccess { AllowedClientIds = [clientA] }));

        var result = await handler.Handle(new GetAllSitesQuery(), CancellationToken.None);

        Assert.That(result.Value!.Select(site => site.Id), Is.EquivalentTo(new[] { siteA.Id }));
    }

    [Test]
    public async Task GetAllSites_NoScope_ReturnsEmpty()
    {
        var handler = new GetAllSitesQueryHandler(
            new FakeSiteRepo([MakeSite(Guid.NewGuid(), "A")]),
            new FakeClientRepo(),
            new FakeScope(new UserScopeAccess()));

        var result = await handler.Handle(new GetAllSitesQuery(), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Value!, Is.Empty);
    }

    [Test]
    public async Task GetAllSites_ResolvesClientName()
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "Cliente X", IsActive = true };
        var site = MakeSite(client.Id, "S");
        var handler = new GetAllSitesQueryHandler(
            new FakeSiteRepo([site]),
            new FakeClientRepo(client),
            new FakeScope(new UserScopeAccess { HasGlobalAccess = true }));

        var result = await handler.Handle(new GetAllSitesQuery(), CancellationToken.None);

        Assert.That(result.Value!.Single().ClientName, Is.EqualTo("Cliente X"));
        Assert.That(result.Value!.Single().ClientActive, Is.True);
    }

    private sealed class FakeClientRepo(params Client[] clients) : IClientRepository
    {
        public Task<IEnumerable<Client>> GetAllAsync(bool includeInactive = false)
            => Task.FromResult<IEnumerable<Client>>(clients);
        public Task<Client?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<Client> CreateAsync(Client client) => throw new NotSupportedException();
        public Task UpdateAsync(Client client) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class FakeScope(UserScopeAccess access) : IScopeContext
    {
        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(access);
        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action) => Task.FromResult(access.HasGlobalAccess);
        public void SetUserId(Guid userId) { }
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }
    }

    private sealed class FakeSiteRepo(List<Site> sites) : ISiteRepository
    {
        public Task<IEnumerable<Site>> GetAllAsync(bool includeInactive = false)
            => Task.FromResult<IEnumerable<Site>>(sites.Where(site => includeInactive || site.IsActive));

        public Task<IEnumerable<Site>> GetByClientIdsAsync(IEnumerable<Guid> clientIds, bool includeInactive = false)
        {
            var ids = clientIds.ToHashSet();
            return Task.FromResult<IEnumerable<Site>>(
                sites.Where(site => ids.Contains(site.ClientId) && (includeInactive || site.IsActive)));
        }

        public Task<IEnumerable<Site>> GetByIdsAsync(IEnumerable<Guid> siteIds, bool includeInactive = false)
        {
            var ids = siteIds.ToHashSet();
            return Task.FromResult<IEnumerable<Site>>(
                sites.Where(site => ids.Contains(site.Id) && (includeInactive || site.IsActive)));
        }

        public Task<Site?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false) => throw new NotSupportedException();
        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }
}
