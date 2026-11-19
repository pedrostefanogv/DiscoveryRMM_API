using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Discovery.IntegrationTests;

[TestFixture]
[Category("Integration")]
[Category("PostgreSQL")]
public sealed class PostgresMigrationAndSchemaTests
{
    private DiscoveryPostgresWebApplicationFactory? _factory;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        await PostgresContainerFixture.EnsureAvailableOrIgnoreAsync();
        _factory = await DiscoveryPostgresWebApplicationFactory.CreateAsync("migrations");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Test]
    public async Task Startup_AppliesAllFluentMigratorMigrations_AndZeroSchemaDriftOnRealPostgres()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        Assert.That(db.Database.IsNpgsql(), Is.True, "Integration test must run against real Npgsql PostgreSQL.");

        var missingTables = await SchemaDriftInspector.FindMissingTablesAsync(db);
        var unreadableTables = await SchemaDriftInspector.FindUnreadableTablesAsync(db);

        Assert.That(missingTables, Is.Empty, "All EF-mapped tables must be created by FluentMigrator.");
        Assert.That(unreadableTables, Is.Empty, "All EF-mapped tables must be readable by the API role.");

        var appliedVersions = await db.Database
            .SqlQueryRaw<long>("SELECT \"Version\" AS \"Value\" FROM \"VersionInfo\" ORDER BY \"Version\"")
            .ToListAsync();
        Assert.That(appliedVersions.Count, Is.GreaterThan(200));
        Assert.That(appliedVersions[^1], Is.GreaterThanOrEqualTo(20260400_000));

        var hasPgVector = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM pg_extension WHERE extname = 'vector'")
            .SingleAsync();
        Assert.That(hasPgVector, Is.EqualTo(1), "pgvector extension must be installed.");

        var seededStates = await db.WorkflowStates.Where(s => s.ClientId == null).CountAsync();
        Assert.That(seededStates, Is.GreaterThanOrEqualTo(5), "DatabaseSeeder must seed default workflow states.");

        using var client = _factory.CreateClient();
        using var healthResponse = await client.GetAsync("/health");
        var healthJson = await healthResponse.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(healthJson);
        var checks = doc.RootElement.GetProperty("checks");
        var pgCheck = checks.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "postgresql");
        var schemaCheck = checks.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "database-schema");

        Assert.That(pgCheck.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
        Assert.That(schemaCheck.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
    }

    [Test]
    public async Task PostgresSchema_EnforcesForeignKeysAndUniqueConstraints()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
        var now = DateTime.UtcNow;

        // 1. Foreign key violation: Site referencing nonexistent ClientId
        var invalidSite = new Site
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Name = "Orphan Site",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Sites.Add(invalidSite);

        var fkEx = await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
        Assert.That(fkEx?.InnerException, Is.InstanceOf<PostgresException>());
        Assert.That(((PostgresException)fkEx!.InnerException!).SqlState, Is.EqualTo(PostgresErrorCodes.ForeignKeyViolation));
        db.ChangeTracker.Clear();

        // 2. Unique constraint violation: duplicate AgentToken.TokenHash
        var client = new Client { Id = Guid.NewGuid(), Name = $"IT-Constraint-{Guid.NewGuid():N}", IsActive = true, CreatedAt = now, UpdatedAt = now };
        var site = new Site { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Constraint Site", IsActive = true, CreatedAt = now, UpdatedAt = now };
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            Hostname = "PG-CONSTRAINT-01",
            Status = AgentStatus.Offline,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        db.Sites.Add(site);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var duplicateHash = new string('a', 64);
        db.AgentTokens.Add(new AgentToken
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            TokenHash = duplicateHash,
            TokenPrefix = "mdz_dup1",
            ExpiresAt = now.AddDays(30),
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        db.AgentTokens.Add(new AgentToken
        {
            Id = Guid.NewGuid(),
            AgentId = agent.Id,
            TokenHash = duplicateHash,
            TokenPrefix = "mdz_dup2",
            ExpiresAt = now.AddDays(30),
            CreatedAt = now
        });

        var uniqueEx = await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
        Assert.That(uniqueEx?.InnerException, Is.InstanceOf<PostgresException>());
        Assert.That(((PostgresException)uniqueEx!.InnerException!).SqlState, Is.EqualTo(PostgresErrorCodes.UniqueViolation));
    }
}
