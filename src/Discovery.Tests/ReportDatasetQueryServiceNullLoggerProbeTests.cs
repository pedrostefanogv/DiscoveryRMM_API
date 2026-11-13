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
/// PROVA + REGRESSAO do CS8604 em ReportDatasetQueryService.cs (CORRIGIDO).
///
/// Historico do defeito: o ctor de 2 args passa logger=null e o ramo "join sem
/// correspondencia" chamava _logger.LogWarning SEM null-conditional. Resultado:
/// ArgumentNullException("logger") no lugar do InvalidOperationException
/// pretendido — erro enganoso que escondia a causa real (datasets sem chave em
/// comum). A linha do defeito era a 294; apos o fix o uso virou
/// _logger?.LogWarning (linha 305) e o throw ficou na 309.
///
/// Este arquivo comecou como prova do defeito (o teste ficava vermelho com o
/// ArgumentNullException) e hoje e a guarda contra a reincidencia: com logger
/// nulo o servico PRECISA entregar o erro de negocio.
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

    /// <summary>
    /// REGRESSAO do CS8604 (corrigido com _logger?.LogWarning em
    /// ReportDatasetQueryService.cs:305). Antes do fix, ESTE MESMO cenario lancava
    /// ArgumentNullException("logger") a partir da linha 294 do servico, escondendo
    /// a causa raiz atras de um erro de logging. O teste original deste arquivo
    /// provava a falha; agora ele prova que a falha NAO volta.
    /// </summary>
    [Test]
    public async Task With_null_logger_the_no_match_branch_throws_InvalidOperationException()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // ctor de 2 ARGS -> _logger == null: o caminho precisa sobreviver a isso
        // e entregar a causa raiz, sem ArgumentNullException do logging.
        var svc = new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions()));

        var erro = await Assert.ThrowsAsync<System.InvalidOperationException>(async () =>
            await svc.QueryAsync(Template(HardwareSensorsLayout), null, CancellationToken.None));

        TestContext.Out.WriteLine("CORRIGIDO: " + erro!.GetType().Name + " -> " + erro.Message);

        Assert.That(erro.Message, Does.Contain("agentId"),
            "a mensagem precisa citar a chave do join");
        Assert.That(erro.Message, Does.Contain("hw"));
        Assert.That(erro.Message, Does.Contain("sns"));
        Assert.That(erro.Message, Does.Contain("nao casou nenhuma linha"),
            "o log do join sem correspondencia nao pode substituir o erro de negocio");
        Assert.That(erro.StackTrace, Does.Contain("ReportDatasetQueryService.cs:line 309"),
            "a origem precisa ser o throw de InvalidOperationException");
    }

    /// <summary>
    /// Guarda contra reincidencia: com logger ausente o servico NUNCA pode lancar
    /// ArgumentNullException/NullReferenceException por causa do logging. A unica
    /// excecao aceitavel neste cenario e o InvalidOperationException de negocio.
    /// </summary>
    [Test]
    public async Task With_null_logger_the_service_never_throws_ArgumentNullException()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var svc = new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions()));

        try
        {
            await svc.QueryAsync(Template(HardwareSensorsLayout), null, CancellationToken.None);
            Assert.Fail("esperava o InvalidOperationException de negocio");
        }
        catch (System.InvalidOperationException)
        {
            // excecao de negocio esperada: o que NAO pode acontecer aqui e
            // ArgumentNullException("logger") vinda do LoggerExtensions.
        }
        catch (System.ArgumentNullException ane)
        {
            Assert.Fail("REINCIDIU o CS8604: logging lancou ArgumentNullException("
                + ane.ParamName + ") em vez do erro de negocio.");
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
