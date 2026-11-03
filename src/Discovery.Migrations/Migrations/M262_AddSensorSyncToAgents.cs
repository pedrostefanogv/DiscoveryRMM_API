using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Colunas do sensor-sync no agent (mesmo papel de last_policy_fingerprint, mas
/// em endpoint próprio). Aditivo: agentes antigos ficam com NULL.
/// </summary>
[Migration(20270101_262)]
public class M262_AddSensorSyncToAgents : Migration
{
    private const string Table = "agents";

    public override void Up()
    {
        if (!Schema.Table(Table).Column("last_sensor_fingerprint").Exists())
        {
            Alter.Table(Table).AddColumn("last_sensor_fingerprint").AsString(128).Nullable();
        }

        if (!Schema.Table(Table).Column("last_sensor_sync_at").Exists())
        {
            Alter.Table(Table).AddColumn("last_sensor_sync_at").AsDateTimeOffset().Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table(Table).Column("last_sensor_sync_at").Exists())
        {
            Delete.Column("last_sensor_sync_at").FromTable(Table);
        }

        if (Schema.Table(Table).Column("last_sensor_fingerprint").Exists())
        {
            Delete.Column("last_sensor_fingerprint").FromTable(Table);
        }
    }
}
