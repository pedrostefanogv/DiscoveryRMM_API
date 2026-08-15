using System.Text.Json;
using Discovery.Core.Cqrs.Logs.Queries;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Enums.Identity;
using Discovery.Core.Interfaces;
using Discovery.Core.Interfaces.Auth;
using Discovery.Infrastructure.Cqrs.Logs;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

        var repository = new LogRepository(db, new FakeAgentMessaging(), NullLogger<LogRepository>.Instance);

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

        var repository = new LogRepository(db, new FakeAgentMessaging(), NullLogger<LogRepository>.Instance);
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
    public async Task ListLogsQueryHandler_ShouldRespectScopeAndReturnPage()
    {
        await using var db = CreateDbContext();

        var clientAllowed = CreateClient("Allowed Client");
        var clientBlocked = CreateClient("Blocked Client");
        var siteAllowed = CreateSite(clientAllowed.Id, "Allowed Site");
        var siteBlocked = CreateSite(clientBlocked.Id, "Blocked Site");

        var matchingLog = CreateLog(clientAllowed.Id, siteAllowed.Id, null, "needle", null);
        var blockedLog = CreateLog(clientBlocked.Id, siteBlocked.Id, null, "needle but blocked", null);

        db.Clients.AddRange(clientAllowed, clientBlocked);
        db.Sites.AddRange(siteAllowed, siteBlocked);
        db.Logs.AddRange(matchingLog, blockedLog);
        await db.SaveChangesAsync();

        var repository = new LogRepository(db, new FakeAgentMessaging(), NullLogger<LogRepository>.Instance);
        var scopeContext = new FakeScopeContext(new UserScopeAccess
        {
            HasGlobalAccess = false,
            AllowedClientIds = [clientAllowed.Id],
            AllowedSiteIds = [siteAllowed.Id]
        });

        var handler = new ListLogsQueryHandler(repository, scopeContext);
        var result = await handler.Handle(new ListLogsQuery(Search: "needle", Limit: 50), CancellationToken.None);

        Assert.That(result.IsSuccess, Is.True);
        var page = result.Value!;
        Assert.That(page.Items.Select(log => log.Id), Is.EquivalentTo(new[] { matchingLog.Id }));
        Assert.That(page.ReturnedItems, Is.EqualTo(1));
    }

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

        public Task<LogSummaryRawDto> GetSummaryAsync(LogQuery query)
            => Task.FromResult(new LogSummaryRawDto(0, [], [], [], [], [], []));

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
