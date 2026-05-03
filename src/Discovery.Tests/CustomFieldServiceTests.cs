using System.Text.Json;
using Discovery.Core.DTOs;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.Interfaces;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Discovery.Tests;

public class CustomFieldServiceTests
{
    [Test]
    public async Task GetSchemaAsync_ShouldReturnMaskedValuesBindingsAndInactiveDefinitions()
    {
        await using var fixture = await CreateFixtureAsync();
        var taskId = Guid.NewGuid();
        var secretDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "agent_secret",
            Label = "Agent Secret",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            IsSecret = true,
            AllowRuntimeRead = true,
            AllowAgentWrite = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var inactiveDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "deployment_ring",
            Label = "Deployment Ring",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Dropdown,
            IsActive = false,
            OptionsJson = JsonSerializer.Serialize(new[] { "critical", "standard" }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.AddRange(secretDefinition, inactiveDefinition);
        fixture.Db.CustomFieldValues.Add(new CustomFieldValue
        {
            Id = Guid.NewGuid(),
            DefinitionId = secretDefinition.Id,
            ScopeType = CustomFieldScopeType.Agent,
            EntityId = fixture.Agent.Id,
            EntityKey = fixture.Agent.Id.ToString("D"),
            ValueJson = JsonSerializer.Serialize("super-secret"),
            UpdatedBy = "tester",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        fixture.Db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
        {
            Id = Guid.NewGuid(),
            DefinitionId = secretDefinition.Id,
            TaskId = taskId,
            CanRead = true,
            CanWrite = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var schema = await fixture.Service.GetSchemaAsync(
            CustomFieldScopeType.Agent,
            fixture.Agent.Id,
            includeInactive: true,
            includeSecrets: false,
            cancellationToken: CancellationToken.None);

        Assert.That(schema, Has.Count.EqualTo(2));

        var secretItem = schema.Single(item => item.DefinitionId == secretDefinition.Id);
        var inactiveItem = schema.Single(item => item.DefinitionId == inactiveDefinition.Id);
        using var secretJson = JsonDocument.Parse(secretItem.ValueJson);

        Assert.Multiple(() =>
        {
            Assert.That(secretItem.EntityId, Is.EqualTo(fixture.Agent.Id));
            Assert.That(secretJson.RootElement.GetString(), Is.EqualTo("***"));
            Assert.That(secretItem.AccessBindings, Has.Count.EqualTo(1));
            Assert.That(secretItem.AccessBindings[0].TaskId, Is.EqualTo(taskId));
            Assert.That(secretItem.AccessBindings[0].CanWrite, Is.True);
            Assert.That(secretItem.ValueUpdatedAt, Is.Not.Null);
            Assert.That(inactiveItem.IsActive, Is.False);
            Assert.That(inactiveItem.Options, Is.EquivalentTo(new[] { "critical", "standard" }));
        });
    }

    [Test]
    public async Task GetRuntimeValuesForAgentAsync_ShouldHonorExecutionBindings()
    {
        await using var fixture = await CreateFixtureAsync();
        var allowedTaskId = Guid.NewGuid();
        var blockedTaskId = Guid.NewGuid();
        var serverDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "global_banner",
            Label = "Global Banner",
            ScopeType = CustomFieldScopeType.Server,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.Public,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var allowedSiteDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "site_token",
            Label = "Site Token",
            ScopeType = CustomFieldScopeType.Site,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var blockedAgentDefinition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "agent_serial",
            Label = "Agent Serial",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowRuntimeRead = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.AddRange(serverDefinition, allowedSiteDefinition, blockedAgentDefinition);
        fixture.Db.CustomFieldExecutionAccesses.AddRange(
            new CustomFieldExecutionAccess
            {
                Id = Guid.NewGuid(),
                DefinitionId = allowedSiteDefinition.Id,
                TaskId = allowedTaskId,
                CanRead = true,
                CanWrite = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CustomFieldExecutionAccess
            {
                Id = Guid.NewGuid(),
                DefinitionId = blockedAgentDefinition.Id,
                TaskId = blockedTaskId,
                CanRead = true,
                CanWrite = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        fixture.Db.CustomFieldValues.AddRange(
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = serverDefinition.Id,
                ScopeType = CustomFieldScopeType.Server,
                EntityKey = "server",
                ValueJson = JsonSerializer.Serialize("maintenance-window"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = allowedSiteDefinition.Id,
                ScopeType = CustomFieldScopeType.Site,
                EntityId = fixture.Site.Id,
                EntityKey = fixture.Site.Id.ToString("D"),
                ValueJson = JsonSerializer.Serialize("site-123"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            new CustomFieldValue
            {
                Id = Guid.NewGuid(),
                DefinitionId = blockedAgentDefinition.Id,
                ScopeType = CustomFieldScopeType.Agent,
                EntityId = fixture.Agent.Id,
                EntityKey = fixture.Agent.Id.ToString("D"),
                ValueJson = JsonSerializer.Serialize("agent-serial"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        await fixture.Db.SaveChangesAsync();

        var values = await fixture.Service.GetRuntimeValuesForAgentAsync(
            fixture.Agent.Id,
            allowedTaskId,
            scriptId: null,
            cancellationToken: CancellationToken.None);

        Assert.That(values.Select(item => item.Name), Is.EquivalentTo(new[] { "global_banner", "site_token" }));

        var siteValue = values.Single(item => item.Name == "site_token");
        using var siteJson = JsonDocument.Parse(siteValue.ValueJson);
        Assert.That(siteJson.RootElement.GetString(), Is.EqualTo("site-123"));
    }

    [Test]
    public async Task UpsertAgentCollectedValueAsync_ShouldPersistValueWhenWriteBindingMatches()
    {
        await using var fixture = await CreateFixtureAsync();
        var taskId = Guid.NewGuid();
        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "agent_runtime_flag",
            Label = "Agent Runtime Flag",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowAgentWrite = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.Add(definition);
        fixture.Db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            TaskId = taskId,
            CanRead = true,
            CanWrite = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var result = await fixture.Service.UpsertAgentCollectedValueAsync(
            fixture.Agent.Id,
            new AgentCustomFieldCollectedValueInput(
                definition.Id,
                null,
                JsonSerializer.Serialize("enabled"),
                taskId,
                null,
                "runtime-agent"),
            CancellationToken.None);

        var stored = await fixture.Db.CustomFieldValues.SingleAsync(item => item.DefinitionId == definition.Id);
        using var resultJson = JsonDocument.Parse(result.ValueJson);

        Assert.Multiple(() =>
        {
            Assert.That(stored.EntityId, Is.EqualTo(fixture.Agent.Id));
            Assert.That(stored.UpdatedBy, Is.EqualTo("runtime-agent"));
            Assert.That(stored.ValueJson, Is.EqualTo(JsonSerializer.Serialize("enabled")));
            Assert.That(resultJson.RootElement.GetString(), Is.EqualTo("enabled"));
        });
    }

    [Test]
    public async Task UpsertAgentCollectedValueAsync_ShouldRejectWriteWithoutMatchingBinding()
    {
        await using var fixture = await CreateFixtureAsync();
        var allowedTaskId = Guid.NewGuid();
        var currentTaskId = Guid.NewGuid();
        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            Name = "script_result",
            Label = "Script Result",
            ScopeType = CustomFieldScopeType.Agent,
            DataType = CustomFieldDataType.Text,
            IsActive = true,
            AllowAgentWrite = true,
            RuntimeAccessMode = CustomFieldRuntimeAccessMode.RestrictedTaskScript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        fixture.Db.CustomFieldDefinitions.Add(definition);
        fixture.Db.CustomFieldExecutionAccesses.Add(new CustomFieldExecutionAccess
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            TaskId = allowedTaskId,
            CanRead = true,
            CanWrite = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        Assert.That(async () => await fixture.Service.UpsertAgentCollectedValueAsync(
                fixture.Agent.Id,
                new AgentCustomFieldCollectedValueInput(
                    null,
                    definition.Name,
                    JsonSerializer.Serialize("blocked"),
                    currentTaskId,
                    null,
                    null),
                CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("Agent is not allowed to write this custom field in the current execution context."));
    }

    private static async Task<CustomFieldFixture> CreateFixtureAsync()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"custom-field-tests-{Guid.NewGuid():N}")
            .Options;

        var db = new TestDiscoveryDbContext(options);

        var now = DateTime.UtcNow;
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Client 01",
            CreatedAt = now,
            UpdatedAt = now
        };
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Name = "Site 01",
            CreatedAt = now,
            UpdatedAt = now
        };
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            Hostname = "agent-01",
            Status = AgentStatus.Online,
            AgentVersion = "1.0.0",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Clients.Add(client);
        db.Sites.Add(site);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        var service = new CustomFieldService(
            db,
            new TestAgentRepository(db),
            new TestSiteRepository(db),
            new TestAgentAutoLabelingService(),
            NullLogger<CustomFieldService>.Instance);

        return new CustomFieldFixture(db, client, site, agent, service);
    }

    private sealed class CustomFieldFixture : IAsyncDisposable
    {
        public CustomFieldFixture(DiscoveryDbContext db, Client client, Site site, Agent agent, CustomFieldService service)
        {
            Db = db;
            Client = client;
            Site = site;
            Agent = agent;
            Service = service;
        }

        public DiscoveryDbContext Db { get; }
        public Client Client { get; }
        public Site Site { get; }
        public Agent Agent { get; }
        public CustomFieldService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    private sealed class TestDiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowedTypes = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(CustomFieldDefinition),
                typeof(CustomFieldValue),
                typeof(CustomFieldExecutionAccess)
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

            modelBuilder.Entity<CustomFieldDefinition>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Name).IsRequired();
                entity.Property(item => item.Label).IsRequired();
            });

            modelBuilder.Entity<CustomFieldValue>(entity =>
            {
                entity.HasKey(item => item.Id);
                entity.Property(item => item.EntityKey).IsRequired();
                entity.Property(item => item.ValueJson).IsRequired();
            });

            modelBuilder.Entity<CustomFieldExecutionAccess>(entity =>
            {
                entity.HasKey(item => item.Id);
            });
        }
    }

    private sealed class TestAgentRepository(DiscoveryDbContext db) : IAgentRepository
    {
        public Task<Agent?> GetByIdAsync(Guid id)
            => db.Agents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);

        public async Task<IEnumerable<Agent>> GetAllAsync()
            => await db.Agents.AsNoTracking().ToListAsync();

        public async Task<IEnumerable<Agent>> GetBySiteIdAsync(Guid siteId)
            => await db.Agents.AsNoTracking().Where(item => item.SiteId == siteId).ToListAsync();

        public async Task<IEnumerable<Agent>> GetByClientIdAsync(Guid clientId)
        {
            return await (
                from agent in db.Agents.AsNoTracking()
                join site in db.Sites.AsNoTracking() on agent.SiteId equals site.Id
                where site.ClientId == clientId
                select agent).ToListAsync();
        }

        public Task<Agent> CreateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateAsync(Agent agent) => throw new NotSupportedException();
        public Task UpdateStatusAsync(Guid id, AgentStatus status, string? ipAddress) => throw new NotSupportedException();
        public Task ApproveZeroTouchAsync(Guid agentId) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
        public Task<IReadOnlyList<Agent>> GetOnlineAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TestSiteRepository(DiscoveryDbContext db) : ISiteRepository
    {
        public Task<Site?> GetByIdAsync(Guid id)
            => db.Sites.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id);

        public async Task<IEnumerable<Site>> GetByClientIdAsync(Guid clientId, bool includeInactive = false)
        {
            var query = db.Sites.AsNoTracking().Where(item => item.ClientId == clientId);
            if (!includeInactive)
                query = query.Where(item => item.IsActive);

            return await query.ToListAsync();
        }

        public Task<Site> CreateAsync(Site site) => throw new NotSupportedException();
        public Task UpdateAsync(Site site) => throw new NotSupportedException();
        public Task DeleteAsync(Guid id) => throw new NotSupportedException();
    }

    private sealed class TestAgentAutoLabelingService : IAgentAutoLabelingService
    {
        public Task EvaluateAgentAsync(Guid agentId, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> HasEnabledRulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ReprocessAllAgentsAsync(string reason, int batchSize = 200, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentLabelRuleDryRunResponse> DryRunAsync(AgentLabelRuleDryRunRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}