using System.Text.Json;
using Discovery.Api.Controllers;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Discovery.Tests;

public class LogsBackendTests
{
    [Test]
    public async Task QueryAsync_ShouldRespectAllowedScopesAndSearchText()
    {
        await using var db = CreateDbContext();

        var clientAllowed = CreateClient("Allowed Client");
        var clientBlocked = CreateClient("Blocked Client");
        var siteAllowed = CreateSite(clientAllowed.Id, "Allowed Site");
        var siteExplicit = CreateSite(clientBlocked.Id, "Explicit Site");
        var siteBlocked = CreateSite(clientBlocked.Id, "Blocked Site");

        var matchingClientLog = CreateLog(clientAllowed.Id, siteAllowed.Id, null, "alpha failure", "{\"traceId\":\"trace-alpha\"}");
        var matchingSiteLog = CreateLog(clientBlocked.Id, siteExplicit.Id, null, "boring message", "{\"context\":\"needle\"}");
        var blockedLog = CreateLog(clientBlocked.Id, siteBlocked.Id, null, "needle but blocked", null);

        db.Clients.AddRange(clientAllowed, clientBlocked);
        db.Sites.AddRange(siteAllowed, siteExplicit, siteBlocked);
        db.Logs.AddRange(matchingClientLog, matchingSiteLog, blockedLog);
        await db.SaveChangesAsync();

        var repository = new LogRepository(db, new FakeAgentMessaging());

        var results = (await repository.QueryAsync(new LogQuery
        {
            HasGlobalAccess = false,
            AllowedClientIds = [clientAllowed.Id],
            AllowedSiteIds = [siteExplicit.Id],
            SearchText = "needle",
            Limit = 50
        })).ToList();

        Assert.That(results.Select(log => log.Id), Is.EquivalentTo(new[] { matchingSiteLog.Id }));
    }

    [Test]
    public async Task QueryPageAsync_ShouldApplyStructuredFilters()
    {
        await using var db = CreateDbContext();

        var client = CreateClient("Client A");
        var site = CreateSite(client.Id, "Site A");
        db.Clients.Add(client);
        db.Sites.Add(site);

        var expected = CreateLog(
            client.Id,
            site.Id,
            null,
            "GET /api/v1/search retornou 500",
            "{\"traceId\":\"trace-123\",\"correlationId\":\"corr-55\",\"path\":\"/api/v1/search\",\"statusCode\":500}");
        var ignored = CreateLog(
            client.Id,
            site.Id,
            null,
            "GET /api/v1/search retornou 404",
            "{\"traceId\":\"trace-999\",\"correlationId\":\"corr-99\",\"path\":\"/api/v1/search\",\"statusCode\":404}");

        db.Logs.AddRange(expected, ignored);
        await db.SaveChangesAsync();

        var repository = new LogRepository(db, new FakeAgentMessaging());
        var results = await repository.QueryPageAsync(new LogQuery
        {
            HasGlobalAccess = true,
            TraceId = "trace-123",
            CorrelationId = "corr-55",
            RequestPath = "/api/v1/search",
            StatusCode = 500,
            Limit = 10
        });

        Assert.That(results.Select(log => log.Id), Is.EquivalentTo(new[] { expected.Id }));
    }

    [Test]
    public async Task Query_ShouldReturnForbidden_WhenClientIsOutsideScope()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(
            db,
            new FakeScopeContext(new UserScopeAccess
            {
                HasGlobalAccess = false,
                AllowedClientIds = [Guid.NewGuid()],
                AllowedSiteIds = []
            }));

        var forbiddenClientId = Guid.NewGuid();
        var result = await controller.Query(
            clientId: forbiddenClientId,
            siteId: null,
            agentId: null,
            type: null,
            level: null,
            source: null,
            search: null,
            traceId: null,
            correlationId: null,
            requestPath: null,
            statusCode: null,
            period: null,
            from: null,
            to: null,
            limit: 100,
            offset: 0);

        Assert.That(result, Is.TypeOf<ObjectResult>());
        var objectResult = (ObjectResult)result;
        Assert.That(objectResult.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task GetScopeOptions_ShouldReturnOnlyEntitiesInsideAccessibleScope()
    {
        await using var db = CreateDbContext();

        var clientA = CreateClient("Client A");
        var clientB = CreateClient("Client B");
        var siteA = CreateSite(clientA.Id, "Site A");
        var siteB = CreateSite(clientB.Id, "Site B");
        var agentA = CreateAgent(siteA.Id, "agent-a");
        var agentB = CreateAgent(siteB.Id, "agent-b");

        db.Clients.AddRange(clientA, clientB);
        db.Sites.AddRange(siteA, siteB);
        db.Agents.AddRange(agentA, agentB);
        await db.SaveChangesAsync();

        var controller = CreateController(
            db,
            new FakeScopeContext(new UserScopeAccess
            {
                HasGlobalAccess = false,
                AllowedClientIds = [],
                AllowedSiteIds = [siteB.Id]
            }));

        var result = await controller.GetScopeOptions();

        Assert.That(result, Is.TypeOf<OkObjectResult>());
        var payload = ((OkObjectResult)result).Value;
        Assert.That(payload, Is.Not.Null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.That(root.GetProperty("canViewAll").GetBoolean(), Is.False);
        Assert.That(root.GetProperty("clients").GetArrayLength(), Is.EqualTo(1));
        Assert.That(root.GetProperty("sites").GetArrayLength(), Is.EqualTo(1));
        Assert.That(root.GetProperty("agents").GetArrayLength(), Is.EqualTo(1));

        Assert.That(root.GetProperty("clients")[0].GetProperty("Id").GetGuid(), Is.EqualTo(clientB.Id));
        Assert.That(root.GetProperty("sites")[0].GetProperty("Id").GetGuid(), Is.EqualTo(siteB.Id));
        Assert.That(root.GetProperty("agents")[0].GetProperty("Id").GetGuid(), Is.EqualTo(agentB.Id));
    }

    [Test]
    public async Task QueryPage_ShouldReturnCursorPage_WhenMoreItemsExist()
    {
        await using var db = CreateDbContext();

        var client = CreateClient("Client Cursor");
        var site = CreateSite(client.Id, "Site Cursor");
        db.Clients.Add(client);
        db.Sites.Add(site);

        var newest = CreateLog(client.Id, site.Id, null, "newest", null, DateTime.UtcNow.AddMinutes(-1));
        var middle = CreateLog(client.Id, site.Id, null, "middle", null, DateTime.UtcNow.AddMinutes(-2));
        var oldest = CreateLog(client.Id, site.Id, null, "oldest", null, DateTime.UtcNow.AddMinutes(-3));

        db.Logs.AddRange(newest, middle, oldest);
        await db.SaveChangesAsync();

        var repository = new LogRepository(db, new FakeAgentMessaging());
        var controller = CreateController(
            db,
            repository,
            new FakeScopeContext(new UserScopeAccess { HasGlobalAccess = true }));

        var firstPageResult = await controller.QueryPage(
            clientId: client.Id,
            siteId: null,
            agentId: null,
            type: null,
            level: null,
            source: null,
            search: null,
            traceId: null,
            correlationId: null,
            requestPath: null,
            statusCode: null,
            period: null,
            from: null,
            to: null,
            cursor: null,
            limit: 2);

        Assert.That(firstPageResult, Is.TypeOf<OkObjectResult>());
        var firstPage = (LogCursorPageDto)((OkObjectResult)firstPageResult).Value!;
        Assert.That(firstPage.ReturnedItems, Is.EqualTo(2));
        Assert.That(firstPage.HasMore, Is.True);
        Assert.That(firstPage.NextCursor, Is.Not.Null.And.Not.Empty);
        Assert.That(firstPage.Items.Select(log => log.Message), Is.EqualTo(new[] { "newest", "middle" }));

        var secondPageResult = await controller.QueryPage(
            clientId: client.Id,
            siteId: null,
            agentId: null,
            type: null,
            level: null,
            source: null,
            search: null,
            traceId: null,
            correlationId: null,
            requestPath: null,
            statusCode: null,
            period: null,
            from: null,
            to: null,
            cursor: firstPage.NextCursor,
            limit: 2);

        var secondPage = (LogCursorPageDto)((OkObjectResult)secondPageResult).Value!;
        Assert.That(secondPage.ReturnedItems, Is.EqualTo(1));
        Assert.That(secondPage.HasMore, Is.False);
        Assert.That(secondPage.Items.Select(log => log.Message), Is.EqualTo(new[] { "oldest" }));
    }

    [Test]
    public async Task QueryPage_ShouldReturnBadRequest_WhenCursorIsInvalid()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(
            db,
            new LogRepository(db, new FakeAgentMessaging()),
            new FakeScopeContext(new UserScopeAccess { HasGlobalAccess = true }));

        var result = await controller.QueryPage(
            clientId: null,
            siteId: null,
            agentId: null,
            type: null,
            level: null,
            source: null,
            search: null,
            traceId: null,
            correlationId: null,
            requestPath: null,
            statusCode: null,
            period: null,
            from: null,
            to: null,
            cursor: "not-base64",
            limit: 50);

        Assert.That(result, Is.TypeOf<BadRequestObjectResult>());
    }

    private static LogsController CreateController(DiscoveryDbContext db, IScopeContext scopeContext)
        => new(
            new FakeLogRepository(),
            new ClientRepository(db),
            new AgentRepository(db),
            new SiteRepository(db),
            scopeContext);

    private static LogsController CreateController(DiscoveryDbContext db, ILogRepository logRepository, IScopeContext scopeContext)
        => new(
            logRepository,
            new ClientRepository(db),
            new AgentRepository(db),
            new SiteRepository(db),
            scopeContext);

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"logs-backend-tests-{Guid.NewGuid():N}")
            .Options;

        return new LogsTestDiscoveryDbContext(options);
    }

    private static Client CreateClient(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static Site CreateSite(Guid clientId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Name = name,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static Agent CreateAgent(Guid siteId, string hostname) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = siteId,
        Hostname = hostname,
        DisplayName = hostname.ToUpperInvariant(),
        Status = AgentStatus.Online,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static LogEntry CreateLog(Guid clientId, Guid siteId, Guid? agentId, string message, string? dataJson, DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        SiteId = siteId,
        AgentId = agentId,
        Type = LogType.System,
        Level = LogLevel.Error,
        Source = LogSource.Api,
        Message = message,
        DataJson = dataJson,
        CreatedAt = createdAt ?? DateTime.UtcNow
    };

    private sealed class FakeScopeContext(UserScopeAccess access) : IScopeContext
    {
        public Guid? ResolvedClientId { get; set; }
        public Guid? ResolvedSiteId { get; set; }

        public Task<UserScopeAccess> GetAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(access);

        public Task<bool> HasGlobalAccessAsync(ResourceType resource, ActionType action)
            => Task.FromResult(access.HasGlobalAccess);

        public void SetUserId(Guid userId)
        {
        }
    }

    private sealed class LogsTestDiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(LogEntry)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null && type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowedTypes.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });

            modelBuilder.Entity<Site>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
            });

            modelBuilder.Entity<Agent>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Hostname).IsRequired();
            });

            modelBuilder.Entity<LogEntry>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Message).IsRequired();
            });
        }
    }

    private sealed class FakeLogRepository : ILogRepository
    {
        public Task<LogEntry> CreateAsync(LogEntry entry)
            => Task.FromResult(entry);

        public Task<IEnumerable<LogEntry>> QueryAsync(LogQuery query)
            => Task.FromResult<IEnumerable<LogEntry>>([]);

        public Task<IReadOnlyList<LogEntry>> QueryPageAsync(LogQuery query)
            => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task<int> PurgeAsync(DateTime cutoff)
            => Task.FromResult(0);
    }

    private sealed class FakeAgentMessaging : IAgentMessaging
    {
        public bool IsConnected => true;

        public Task PublishClientFanoutCommandAsync(Guid clientId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishDashboardEventAsync(DashboardEventMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishGlobalFanoutCommandAsync(CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishP2pDiscoverySnapshotAsync(Guid clientId, Guid siteId, string payload, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishSiteFanoutCommandAsync(Guid clientId, Guid siteId, CommandDispatchEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishSyncPingAsync(Guid agentId, SyncInvalidationPingMessage ping, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendCommandAsync(Guid agentId, Guid commandId, string commandType, string payload)
            => Task.CompletedTask;

        public Task SubscribeToAgentMessagesAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
