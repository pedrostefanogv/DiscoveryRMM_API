using Discovery.Core.Configuration;
using NUnit.Framework;

namespace Discovery.Tests;

/// <summary>
/// Política de manutenção física: defaults seguros, normalização de valores
/// inválidos e leitura tolerante do RetentionSettingsJson.
/// </summary>
public class DatabaseMaintenanceSettingsTests
{
    [Test]
    public void Parse_WithEmptyJson_ReturnsSafeDefaults()
    {
        var settings = DatabaseMaintenanceSettings.Parse("{}");

        Assert.That(settings.Enabled, Is.True);
        Assert.That(settings.VacuumFull, Is.False, "VACUUM FULL não pode ser default (lock exclusivo)");
        Assert.That(settings.VacuumAnalyze, Is.True);
        Assert.That(settings.ReindexScope, Is.EqualTo(DatabaseReindexScopes.Invalid));
        Assert.That(settings.VacuumFullScope, Is.EqualTo(DatabaseMaintenanceScopes.Bloated));
        Assert.That(settings.Schedule, Is.EqualTo("0 0 3 ? * SUN"));
        Assert.That(settings.CreateMissingForeignKeyIndexes, Is.False, "criação de índice muda schema: opt-in");
        Assert.That(settings.ForeignKeyIndexPrefix, Is.EqualTo("ix"));
    }

    [Test]
    public void Parse_WithNull_ReturnsDefaults()
    {
        var settings = DatabaseMaintenanceSettings.Parse(null);

        Assert.That(settings.Enabled, Is.True);
        Assert.That(settings.LockTimeoutSeconds, Is.EqualTo(5));
    }

    [Test]
    public void Parse_ReadsConfiguredSection()
    {
        var json = """
            {
              "logRetentionDays": 30,
              "databaseMaintenance": {
                "enabled": true,
                "schedule": "0 0 4 ? * SAT",
                "vacuumFull": true,
                "vacuumFullScope": "all",
                "reindexScope": "all",
                "lockTimeoutSeconds": 30
              }
            }
            """;

        var settings = DatabaseMaintenanceSettings.Parse(json);

        Assert.That(settings.Schedule, Is.EqualTo("0 0 4 ? * SAT"));
        Assert.That(settings.VacuumFull, Is.True);
        Assert.That(settings.VacuumFullScope, Is.EqualTo(DatabaseMaintenanceScopes.All));
        Assert.That(settings.ReindexScope, Is.EqualTo(DatabaseReindexScopes.All));
        Assert.That(settings.LockTimeoutSeconds, Is.EqualTo(30));
    }

    [Test]
    public void Parse_WithInvalidJson_FallsBackToDefaults()
    {
        var settings = DatabaseMaintenanceSettings.Parse("{ isso não é json");

        Assert.That(settings.Enabled, Is.True);
        Assert.That(settings.MaintenanceWorkMemMb, Is.EqualTo(256));
    }

    [Test]
    public void Parse_ClampsAndNormalizesOutOfRangeValues()
    {
        var json = """
            {
              "databaseMaintenance": {
                "vacuumFullBloatPercent": -50,
                "lockTimeoutSeconds": 99999,
                "maintenanceWorkMemMb": 1,
                "freezeMinXidAge": 1,
                "maxTablesPerRun": -3,
                "reindexScope": "banana",
                "schedule": "   "
              }
            }
            """;

        var settings = DatabaseMaintenanceSettings.Parse(json);

        Assert.That(settings.VacuumFullBloatPercent, Is.EqualTo(1));
        Assert.That(settings.LockTimeoutSeconds, Is.EqualTo(3600));
        Assert.That(settings.MaintenanceWorkMemMb, Is.EqualTo(16));
        Assert.That(settings.FreezeMinXidAge, Is.EqualTo(1_000_000));
        Assert.That(settings.MaxTablesPerRun, Is.EqualTo(0));
        Assert.That(settings.ReindexScope, Is.EqualTo(DatabaseReindexScopes.Invalid));
        Assert.That(settings.Schedule, Is.EqualTo("0 0 3 ? * SUN"));
        Assert.That(settings.ForeignKeyIndexPrefix, Is.EqualTo("ix"));
    }

    [Test]
    public void Parse_EnablesForeignKeyIndexCreationAndNormalizesPrefix()
    {
        var json = """
            {
              "databaseMaintenance": {
                "createMissingForeignKeyIndexes": true,
                "foreignKeyIndexPrefix": "  IDX_FK  "
              }
            }
            """;

        var settings = DatabaseMaintenanceSettings.Parse(json);

        Assert.That(settings.CreateMissingForeignKeyIndexes, Is.True);
        Assert.That(settings.ForeignKeyIndexPrefix, Is.EqualTo("idx_fk"));
    }
}
