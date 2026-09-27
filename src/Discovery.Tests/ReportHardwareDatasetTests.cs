using Discovery.Core.Entities;
using Discovery.Core.Enums;
using Discovery.Infrastructure.Data;
using Discovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Discovery.Tests;

/// <summary>
/// Datasets de componentes de hardware (Discos, Rede, Portas, Impressoras) são
/// derivados do JSON em agent_hardware_info — as tabelas dedicadas não existem
/// (M052 removeu as antigas e nunca houve migration para disk_infos/...).
/// </summary>
public class ReportHardwareDatasetTests
{
    private const string ComponentsJson = """
    {
      "disks": [
        {
          "id": "11111111-1111-1111-1111-111111111111",
          "driveLetter": "C:",
          "totalSizeBytes": 1000,
          "freeSpaceBytes": 250,
          "fileSystem": "NTFS",
          "mediaType": "SSD",
          "collectedAt": "2026-01-01T00:00:00Z"
        }
      ],
      "networkAdapters": [
        {
          "id": "22222222-2222-2222-2222-222222222222",
          "name": "Ethernet",
          "macAddress": "AA:BB:CC:DD:EE:FF",
          "ipAddress": "10.0.0.10",
          "speed": "1000 Mbps",
          "collectedAt": "2026-01-01T00:00:00Z"
        }
      ],
      "listeningPorts": [
        {
          "id": "33333333-3333-3333-3333-333333333333",
          "processName": "svc",
          "processId": 42,
          "protocol": "TCP",
          "address": "0.0.0.0",
          "port": 443,
          "collectedAt": "2026-01-01T00:00:00Z"
        }
      ],
      "printers": [
        {
          "id": "44444444-4444-4444-4444-444444444444",
          "name": "HP Laser",
          "driverName": "hp.dll",
          "portName": "USB001",
          "isDefault": true,
          "shared": true,
          "collectedAt": "2026-01-01T00:00:00Z"
        }
      ]
    }
    """;

    private static DiscoveryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseInMemoryDatabase($"report-hardware-{Guid.NewGuid():N}")
            .Options;
        return new ReportHardwareTestDbContext(options);
    }

    private static async Task<(DiscoveryDbContext Db, ReportDatasetQueryService Service)> BuildServiceAsync()
    {
        var db = CreateDbContext();

        var clientId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        db.Clients.Add(new Client { Id = clientId, Name = "Acme", IsActive = true });
        db.Sites.Add(new Site { Id = siteId, ClientId = clientId, Name = "Matriz", IsActive = true });
        db.Agents.Add(new Agent
        {
            Id = agentId,
            SiteId = siteId,
            Hostname = "PC-01",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.AgentHardwareInfos.Add(new AgentHardwareInfo
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            HardwareComponentsJson = ComponentsJson,
            CollectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return (db, new ReportDatasetQueryService(db, new MemoryCache(new MemoryCacheOptions())));
    }

    private static ReportTemplate Template(ReportDatasetType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = "template",
        DatasetType = type,
        LayoutJson = "{}"
    };

    [Test]
    public async Task DiskDataset_IsBuiltFromHardwareComponentsJson()
    {
        var (db, service) = await BuildServiceAsync();
        using var _ = db;

        var result = await service.QueryAsync(Template(ReportDatasetType.AgentDisks), null, CancellationToken.None);

        Assert.That(result.Rows, Has.Count.EqualTo(1));
        Assert.That(result.Rows[0]["agentHostname"], Is.EqualTo("PC-01"));
        Assert.That(result.Rows[0]["clientName"], Is.EqualTo("Acme"));
        Assert.That(result.Rows[0]["diskName"], Is.EqualTo("C:"));
        Assert.That(result.Rows[0]["sizeBytes"], Is.EqualTo(1000L));
        Assert.That(result.Rows[0]["freeBytes"], Is.EqualTo(250L));
        Assert.That(result.Rows[0]["type"], Is.EqualTo("SSD"));
    }

    [Test]
    public async Task NetworkDataset_IsBuiltFromHardwareComponentsJson()
    {
        var (db, service) = await BuildServiceAsync();
        using var _ = db;

        var result = await service.QueryAsync(Template(ReportDatasetType.NetworkAdapters), null, CancellationToken.None);

        Assert.That(result.Rows, Has.Count.EqualTo(1));
        Assert.That(result.Rows[0]["adapterName"], Is.EqualTo("Ethernet"));
        Assert.That(result.Rows[0]["ipAddresses"], Is.EqualTo("10.0.0.10"));
        Assert.That(result.Rows[0]["macAddress"], Is.EqualTo("AA:BB:CC:DD:EE:FF"));
    }

    [Test]
    public async Task ListeningPortsDataset_IsBuiltFromHardwareComponentsJson()
    {
        var (db, service) = await BuildServiceAsync();
        using var _ = db;

        var result = await service.QueryAsync(Template(ReportDatasetType.ListeningPorts), null, CancellationToken.None);

        Assert.That(result.Rows, Has.Count.EqualTo(1));
        Assert.That(result.Rows[0]["port"], Is.EqualTo(443));
        Assert.That(result.Rows[0]["protocol"], Is.EqualTo("TCP"));
        Assert.That(result.Rows[0]["processName"], Is.EqualTo("svc"));
        Assert.That(result.Rows[0]["state"], Is.EqualTo("LISTEN"));
    }

    [Test]
    public async Task PrintersDataset_IsBuiltFromHardwareComponentsJson()
    {
        var (db, service) = await BuildServiceAsync();
        using var _ = db;

        var result = await service.QueryAsync(Template(ReportDatasetType.Printers), null, CancellationToken.None);

        Assert.That(result.Rows, Has.Count.EqualTo(1));
        Assert.That(result.Rows[0]["printerName"], Is.EqualTo("HP Laser"));
        Assert.That(result.Rows[0]["isShared"], Is.EqualTo(true));
    }

    private sealed class ReportHardwareTestDbContext(DbContextOptions<DiscoveryDbContext> options) : DiscoveryDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var allowed = new HashSet<Type>
            {
                typeof(Client),
                typeof(Site),
                typeof(Agent),
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
            modelBuilder.Entity<AgentHardwareInfo>(entity => entity.HasKey(item => item.Id));
        }
    }
}
