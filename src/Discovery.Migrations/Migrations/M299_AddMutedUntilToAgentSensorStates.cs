using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Silenciamento temporário (snooze) de alertas por estado de sensor/alvo.
/// </summary>
[Migration(20261125_299)]
public class M299_AddMutedUntilToAgentSensorStates : Migration
{
    private const string Table = "agent_sensor_states";

    public override void Up()
    {
        if (!Schema.Table(Table).Column("muted_until").Exists())
        {
            Alter.Table(Table)
                .AddColumn("muted_until")
                .AsDateTimeOffset()
                .Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Column("muted_until").Exists())
        {
            Delete.Column("muted_until").FromTable(Table);
        }
    }
}
