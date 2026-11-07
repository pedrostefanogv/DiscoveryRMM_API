using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// PROVA do CS8604 em ReportDatasetQueryService.cs:294 (reproduzido).
///
/// O ctor de 2 args (ReportDatasetQueryService.cs:32-34) passa logger=null.
/// O ramo "join sem correspondencia" (:287-299) chama _logger.LogWarning
/// SEM null-conditional. Consequencia: NullReferenceException no lugar do
/// InvalidOperationException pretendido — erro enganoso que esconde a causa
/// real (datasets sem chave em comum).
///
/// Reproduz o cenario de ReportMultiSourceJoinTests.MultiSource_JoinWithNoMatches_FailsLoudly
/// mudando APENAS o construtor: 3 args (NullLogger) -> 2 args (logger=null).
/// </summary>
[TestFixture]
public class ReportDatasetQueryServiceNullLoggerProbeTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid SiteId = Guid.NewGuid();
    private static readonly Guid AgentA = Guid.NewGuid();

    private const string HardwareSensorsLayout = """
    {
      "dataSources": [
        { "datasetType": 4, "alias": "hw" },
        { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
      ],
      "columns": [
        { "field": "hw.agentHostname" },
        { "field": "hw.processor" }
      ]
    }
    """;

    private static DiscoveryDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"null-logger-probe-{Guid.NewGuid():N}")
            .Options;
        return new ProbeDbContext(options);
    }

    private static ReportTemplate Template(string layoutJson) => new()
    {
        Id = Guid.NewGuid(),
        Name = "template",
        DatasetType = ReportDatasetType.AgentHardware,
        LayoutJson = layoutJson
    };

    private static async Task SeedAsync(DiscoveryDbContext db)
    {
        var now = DateTime.UtcNow;
        db.Clients.Add(new Client { Id = ClientId, Name = "Acme", IsActive = true });
        db.Sites.Add(new Site { Id = SiteId, ClientId = ClientId, Name = "Matriz", IsActive = true });
        db.Agents.Add(new Agent
        {
            Id = AgentA, SiteId = SiteId, Hostname = "PC-01",
            OperatingSystem = "Windows 11 Pro", OsVersion = "10.0",
            AgentVersion = "1.2.1", Status = AgentStatus.Online,
            LastSeenAt = now, CreatedAt = now, UpdatedAt = now
        });
        db.AgentHardwareInfos.Add(new AgentHardwareInfo
        {
            Id = Guid.NewGuid(), AgentId = AgentA,
            OsName = "Windows 11 Pro", Processor = "Ryzen 7",
            TotalMemoryBytes = 16_000_000_000L, TotalDisksCount = 2,
            CollectedAt = now, InventoryCollectedAt = now
        });
        // NENHUM sensor cadastrado => o join nao casa NENHUMA linha.
        await db.SaveChangesAsync();
    }

    [Test]
    public void Two_arg_ctor_leaves_logger_null()
    {
        using var db = NewDb();
        var svc = new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions()));
        var f = typeof(ReportDatasetQueryService).GetField("_logger",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(f!.GetValue(svc), Is.Null, "o ctor de 2 args deixa _logger null");
    }

    [Test]
    public async Task With_null_logger_the_no_match_branch_throws_ArgumentNullException()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // ctor de 2 ARGS -> _logger == null  (o caso do defeito)
        var svc = new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions()));

        try
        {
            await svc.QueryAsync(Template(HardwareSensorsLayout), null, CancellationToken.None);
            Assert.Fail("esperava excecao");
        }
        catch (System.ArgumentNullException ane)
        {
            // LoggerExtensions.LogWarning lanca ArgumentNullException quando o
            // receiver e null (nao NRE, porque o metodo valida o parametro).
            TestContext.Out.WriteLine("REPRODUZIDO: " + ane.GetType().Name + " -> " + ane.Message);
            Assert.That(ane.ParamName, Is.EqualTo("logger"));
            Assert.That(ane.StackTrace, Does.Contain("ReportDatasetQueryService.cs:line 294"),
                "a origem precisa ser a linha 294");
            Assert.Pass("CS8604 CONFIRMADO na linha 294: ArgumentNullException(logger) " +
                        "em vez do InvalidOperationException pretendido.");
        }
        catch (System.InvalidOperationException ioe)
        {
            Assert.Fail("NAO reproduziu: lancou InvalidOperationException (" + ioe.Message + ")");
        }
    }

    private sealed class ProbeDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client), typeof(Site), typeof(Agent), typeof(AgentLabel),
                typeof(AgentSensorState), typeof(SensorDefinition), typeof(AgentHardwareInfo)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(t => t.IsClass && t.Namespace is not null &&
                                     t.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(t => !allowed.Contains(t)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(e => e.HasKey(i => i.Id));
            modelBuilder.Entity<Site>(e => e.HasKey(i => i.Id));
            modelBuilder.Entity<Agent>(e => e.HasKey(i => i.Id));
            modelBuilder.Entity<AgentLabel>(e => e.HasKey(i => i.Id));
            modelBuilder.Entity<AgentSensorState>(e => e.HasKey(i => i.Id));
            modelBuilder.Entity<SensorDefinition>(e => e.HasKey(i => i.Id));
            modelBuilder.Entity<AgentHardwareInfo>(e => e.HasKey(i => i.Id));
        }
    }
}
