using Discovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Discovery.Tests;

/// <summary>
/// Guardas de schema dos sensores. O caso crítico é a unicidade do evento:
/// multi-volume (disco "todos os volumes") compartilha o runId entre alvos —
/// sem target_key na chave, duas mudanças no mesmo ciclo violam o índice.
/// </summary>
public class SensorSchemaTests
{
    private static DiscoveryDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DiscoveryDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=discovery_test;Username=postgres;Password=postgres",
                npgsql => npgsql.UseVector())
            .Options;

        return new DiscoveryDbContext(options);
    }

    [Test]
    public void SensorEvent_UniqueIndex_IncludesTargetKey()
    {
        using var db = NewContext();
        var entity = db.Model.FindEntityType(typeof(Discovery.Core.Entities.AgentSensorEvent));
        Assert.That(entity, Is.Not.Null);

        var index = entity!.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "ux_sensor_events_agent_run_kind");

        Assert.That(index, Is.Not.Null, "índice de unicidade do evento de sensor não encontrado");
        Assert.That(index!.IsUnique, Is.True);

        var properties = index.Properties.Select(p => p.Name).ToList();
        Assert.That(properties, Does.Contain("TargetKey"),
            "sem TargetKey, sensores multi-volume violam a unicidade ao mudar 2 alvos no mesmo run");
    }

    [Test]
    public void SensorSample_UniqueIndex_IncludesTargetKey()
    {
        using var db = NewContext();
        var entity = db.Model.FindEntityType(typeof(Discovery.Core.Entities.AgentSensorSample));
        Assert.That(entity, Is.Not.Null);

        var index = entity!.GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "ux_sensor_samples_run");

        Assert.That(index, Is.Not.Null);
        Assert.That(index!.Properties.Select(p => p.Name), Does.Contain("TargetKey"));
    }
}
