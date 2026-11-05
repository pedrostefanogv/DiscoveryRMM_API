using System.Text.Json;
using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Core.ValueObjects;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Execucao do join multi-source. Cobre a regressao em que o relatorio saia
/// "pronto" mas com as colunas da segunda fonte 100% nulas, porque nenhuma
/// linha casava e nao havia erro nem log.
/// </summary>
[TestFixture]
public class ReportMultiSourceJoinTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid SiteId = Guid.NewGuid();
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"report-multisource-{Guid.NewGuid():N}")
            .Options;
        return new ReportJoinTestDbContext(options);
    }

    private static async Task<(DiscoveryDbContext Db, ReportDatasetQueryService Service)> BuildAsync()
    {
        var db = CreateDbContext();
        var now = DateTime.UtcNow;

        db.Clients.Add(new Client { Id = ClientId, Name = "Acme", IsActive = true });
        db.Sites.Add(new Site { Id = SiteId, ClientId = ClientId, Name = "Matriz", IsActive = true });
        db.Agents.AddRange(
            new Agent
            {
                Id = AgentA, SiteId = SiteId, Hostname = "PC-01",
                OperatingSystem = "Windows 11 Pro", OsVersion = "10.0",
                AgentVersion = "1.2.1", Status = AgentStatus.Online,
                LastSeenAt = now, CreatedAt = now, UpdatedAt = now
            },
            new Agent
            {
                Id = AgentB, SiteId = SiteId, Hostname = "PC-02",
                OperatingSystem = "Windows 10 Pro", OsVersion = "10.0",
                AgentVersion = "1.2.1", Status = AgentStatus.Offline,
                LastSeenAt = now.AddHours(-2), CreatedAt = now, UpdatedAt = now
            });

        // O dataset AgentHardware le de AgentHardwareInfos (1:1 com Agent).
        db.AgentHardwareInfos.AddRange(
            new AgentHardwareInfo
            {
                Id = Guid.NewGuid(), AgentId = AgentA,
                OsName = "Windows 11 Pro", Processor = "Ryzen 7",
                TotalMemoryBytes = 16_000_000_000L, TotalDisksCount = 2,
                CollectedAt = now, InventoryCollectedAt = now
            },
            new AgentHardwareInfo
            {
                Id = Guid.NewGuid(), AgentId = AgentB,
                OsName = "Windows 10 Pro", Processor = "Core i5",
                TotalMemoryBytes = 8_000_000_000L, TotalDisksCount = 1,
                CollectedAt = now, InventoryCollectedAt = now
            });

        await db.SaveChangesAsync();

        var service = new ReportDatasetQueryService(
            db, new MemoryCache(new MemoryCacheOptions()), NullLogger<ReportDatasetQueryService>.Instance);
        return (db, service);
    }

    /// <summary>
    /// Um sensor completo = definicao (nome/tipo) + estado por agente. A
    /// consulta do dataset faz join com SensorDefinitions para o nome.
    /// </summary>
    private static void SeedSensor(DiscoveryDbContext db, Guid agentId, string key, string name)
    {
        var definition = new SensorDefinition
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = name,
            SensorType = SensorType.Script,
            ScopeType = AppApprovalScopeType.Agent,
            AgentId = agentId,
            IsActive = true
        };
        db.SensorDefinitions.Add(definition);
        db.AgentSensorStates.Add(new AgentSensorState
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            SensorId = definition.Id,
            Status = SensorStatus.Passing,
            Severity = SensorSeverity.Info,
            TargetKey = string.Empty,
            LastRunAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }

    private static ReportTemplate Template(string layoutJson) => new()
    {
        Id = Guid.NewGuid(),
        Name = "template",
        DatasetType = ReportDatasetType.AgentHardware,
        LayoutJson = layoutJson
    };

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

    [Test]
    public async Task MultiSource_HardwareAndSensors_PopulatesJoinedColumns()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        // Sensores cadastrados para os dois agentes.
        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        SeedSensor(db, AgentB, "disk.free", "Disco livre");
        await db.SaveChangesAsync();

        var result = await service.QueryAsync(Template(HardwareSensorsLayout), null, CancellationToken.None);

        Assert.That(result.Rows, Is.Not.Empty);
        // A coluna qualificada do join precisa estar PREENCHIDA (era o bug:
        // colunas do right 100% nulas).
        foreach (var row in result.Rows)
        {
            Assert.That(row["sns.sensorName"], Is.Not.Null,
                "coluna sns.sensorName nao foi populada pelo join");
        }

        var pc01 = result.Rows.Single(r => (string?)r["hw.agentHostname"] == "PC-01");
        Assert.That(pc01["sns.sensorName"], Is.EqualTo("Carga de CPU"));
    }

    [Test]
    public async Task MultiSource_JoinWithNoMatches_FailsLoudly()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;
        // Nenhum sensor cadastrado => o join nao casa NENHUMA linha.

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.QueryAsync(Template(HardwareSensorsLayout), null, CancellationToken.None));

        Assert.That(erro!.Message, Does.Contain("agentId"));
        Assert.That(erro.Message, Does.Contain("hw"));
        Assert.That(erro.Message, Does.Contain("sns"));
    }

    [Test]
    public async Task MultiSource_LeftJoin_KeepsBaseRowsWithoutMatch()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        // So o PC-01 tem sensor: o PC-02 precisa sobreviver com sns.* nulo
        // quando pelo menos parte da base casa (left join).
        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        await db.SaveChangesAsync();

        var result = await service.QueryAsync(Template(HardwareSensorsLayout), null, CancellationToken.None);

        var hostnames = result.Rows.Select(r => (string?)r["hw.agentHostname"]).ToList();
        Assert.That(hostnames, Does.Contain("PC-01"));
        Assert.That(hostnames, Does.Contain("PC-02"), "left join precisa preservar a linha sem match");

        var pc02 = result.Rows.Single(r => (string?)r["hw.agentHostname"] == "PC-02");
        Assert.That(pc02["sns.sensorName"], Is.Null);
    }

    [Test]
    public async Task MultiSource_InnerJoin_DropsBaseRowsWithoutMatch()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        await db.SaveChangesAsync();

        const string innerLayout = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "inner" } }
          ],
          "columns": [ { "field": "hw.agentHostname" } ]
        }
        """;

        var result = await service.QueryAsync(Template(innerLayout), null, CancellationToken.None);

        var hostnames = result.Rows.Select(r => (string?)r["hw.agentHostname"]).ToList();
        Assert.That(hostnames, Does.Contain("PC-01"));
        Assert.That(hostnames, Does.Not.Contain("PC-02"), "inner join descarta a linha sem match");
    }

    [Test]
    public async Task MultiSource_ChainedJoin_A_B_C()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        // A terceira fonte (labels) tambem precisa ter linha: uma fonte vazia
        // agora falha alto em vez de gerar relatorio silenciosamente vazio.
        db.AgentLabels.Add(new AgentLabel
        {
            Id = Guid.NewGuid(), AgentId = AgentA, Label = "producao",
            SourceType = AgentLabelSourceType.Manual, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // hw <- sns <- labels (encadeado: a terceira fonte se liga a hw).
        const string chained = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } },
            { "datasetType": 6, "alias": "lbl", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
          ],
          "columns": [ { "field": "hw.agentHostname" }, { "field": "sns.sensorName" } ]
        }
        """;

        var result = await service.QueryAsync(Template(chained), null, CancellationToken.None);

        Assert.That(result.Rows, Is.Not.Empty);
        var row = result.Rows.Single(r => (string?)r["hw.agentHostname"] == "PC-01");
        Assert.That(row["sns.sensorName"], Is.EqualTo("Carga de CPU"));
    }

    [Test]
    public async Task MultiSource_JoinKeysAreCaseInsensitiveWhereExpected()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        // O layout usa MAIUSCULAS na chave: o match precisa continuar casando.
        const string upperLayout = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "AGENTID", "targetKey": "AGENTID", "joinType": "left" } }
          ],
          "columns": [ { "field": "hw.agentHostname" } ]
        }
        """;

        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        await db.SaveChangesAsync();

        var result = await service.QueryAsync(Template(upperLayout), null, CancellationToken.None);

        var pc01 = result.Rows.Single(r => (string?)r["hw.agentHostname"] == "PC-01");
        Assert.That(pc01["sns.sensorName"], Is.EqualTo("Carga de CPU"));
    }

    // ── Fatia D: fonte principal explicita (base:true) ───────────────────────

    /// <summary>Layout equivalente ao HardwareSensorsLayout, mas com a base
    /// MARCADA na segunda fonte: a ordem do array deixa de definir a principal.</summary>
    private const string BaseMarkedSecondLayout = """
    {
      "dataSources": [
        { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } },
        { "datasetType": 4, "alias": "hw", "base": true }
      ],
      "columns": [
        { "field": "hw.agentHostname" },
        { "field": "hw.processor" }
      ]
    }
    """;

    /// <summary>
    /// A base marcada na posicao 1 precisa produzir a MESMA saida que o layout
    /// equivalente com a base em primeiro (HardwareSensorsLayout). Sem a
    /// promocao, o join usaria "sns" como base e o resultado seria outro.
    /// </summary>
    [Test]
    public async Task MultiSource_BaseMarkedSecond_ProducesSameResultAsBaseFirst()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        SeedSensor(db, AgentB, "disk.free", "Disco livre");
        await db.SaveChangesAsync();

        var baseFirst = await service.QueryAsync(Template(HardwareSensorsLayout), null, CancellationToken.None);
        var baseMarkedSecond = await service.QueryAsync(Template(BaseMarkedSecondLayout), null, CancellationToken.None);

        Assert.That(baseMarkedSecond.Columns, Is.EqualTo(baseFirst.Columns),
            "as colunas precisam ser as mesmas nas duas formas de declarar a base");
        Assert.That(baseMarkedSecond.Rows, Has.Count.EqualTo(baseFirst.Rows.Count));

        // A base e "hw" nos dois casos: as linhas de hardware aparecem integras.
        var hostnames = baseMarkedSecond.Rows.Select(row => (string?)row["hw.agentHostname"]).ToList();
        Assert.That(hostnames, Does.Contain("PC-01"));
        Assert.That(hostnames, Does.Contain("PC-02"));

        var pc01 = baseMarkedSecond.Rows.Single(row => (string?)row["hw.agentHostname"] == "PC-01");
        Assert.That(pc01["sns.sensorName"], Is.EqualTo("Carga de CPU"),
            "o join precisa ter casado em 'hw' apesar de 'sns' vir primeiro no array");
    }

    /// <summary>
    /// REGRESSAO DE ADITIVIDADE: sem a marca "base", nada e reordenado. A
    /// primeira fonte do array continua sendo a base (comportamento historico),
    /// entao um layout que coloca "sns" primeiro continua usando sns como base.
    /// </summary>
    [Test]
    public async Task MultiSource_WithoutBaseMark_KeepsArrayOrderAsBase()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        await db.SaveChangesAsync();

        // Mesmo array do BaseMarkedSecondLayout, mas SEM "base": aqui quem manda
        // e a ordem, entao "sns" e a base.
        const string noMarkSnsFirst = """
        {
          "dataSources": [
            { "datasetType": 29, "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } },
            { "datasetType": 4, "alias": "hw" }
          ],
          "columns": [
            { "field": "hw.agentHostname" }
          ]
        }
        """;

        var result = await service.QueryAsync(Template(noMarkSnsFirst), null, CancellationToken.None);

        // "sns" e a base => exatamente uma linha por sensor (so o PC-01 tem um).
        Assert.That(result.Rows, Has.Count.EqualTo(1),
            "sem a marca a base e dataSources[0] (sns); a ordem nao pode ser reordenada");
        Assert.That(result.Rows[0]["sns.sensorName"], Is.EqualTo("Carga de CPU"));
    }

    // ── Fatia G: fontes invalidas nao podem ser descartadas em silencio ──────

    [Test]
    public async Task MultiSource_SourceWithoutDatasetType_FailsInsteadOfBeingDropped()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        await db.SaveChangesAsync();

        // A 2a fonte nao tem datasetType. Antes ela era REMOVIDA em silencio e,
        // sobrando uma fonte so, o relatorio voltava "com sucesso".
        const string layout = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "alias": "sns", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
          ],
          "columns": [ { "field": "hw.agentHostname" } ]
        }
        """;

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.QueryAsync(Template(layout), null, CancellationToken.None));

        Assert.That(erro!.Message, Does.Contain("dataSources[1]"),
            "a mensagem precisa citar o indice da fonte invalida");
        Assert.That(erro.Message, Does.Contain("sns"),
            "a mensagem precisa citar o alias da fonte invalida");
        Assert.That(erro.Message, Does.Contain("datasetType"));
    }

    [Test]
    public async Task MultiSource_SourceWithEmptyAlias_FailsInsteadOfBeingDropped()
    {
        var (db, service) = await BuildAsync();
        using var _ = db;

        SeedSensor(db, AgentA, "cpu.load", "Carga de CPU");
        await db.SaveChangesAsync();

        // A 2a fonte tem alias vazio: tambem era removida em silencio.
        const string layout = """
        {
          "dataSources": [
            { "datasetType": 4, "alias": "hw" },
            { "datasetType": 29, "alias": "   ", "join": { "joinToAlias": "hw", "sourceKey": "agentId", "targetKey": "agentId", "joinType": "left" } }
          ],
          "columns": [ { "field": "hw.agentHostname" } ]
        }
        """;

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.QueryAsync(Template(layout), null, CancellationToken.None));

        Assert.That(erro!.Message, Does.Contain("dataSources[1]"),
            "a mensagem precisa citar o indice da fonte com alias vazio");
        Assert.That(erro.Message, Does.Contain("alias"),
            "a mensagem precisa apontar o alias como problema");
    }

    private sealed class ReportJoinTestDbContext(DbContextOptions<DiscoveryDbContext> options)
        : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
                typeof(AgentLabel),
                typeof(AgentSensorState),
                typeof(SensorDefinition),
                typeof(AgentHardwareInfo)
            };

            foreach (var entityType in typeof(Client).Assembly.GetTypes()
                         .Where(type => type.IsClass && type.Namespace is not null &&
                                        type.Namespace.StartsWith("Discovery.Core.Entities", StringComparison.Ordinal))
                         .Where(type => !allowed.Contains(type)))
            {
                modelBuilder.Ignore(entityType);
            }

            modelBuilder.Entity<Client>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Site>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<Agent>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentLabel>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentSensorState>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<SensorDefinition>(entity => entity.HasKey(item => item.Id));
            modelBuilder.Entity<AgentHardwareInfo>(entity => entity.HasKey(item => item.Id));
        }
    }
}
