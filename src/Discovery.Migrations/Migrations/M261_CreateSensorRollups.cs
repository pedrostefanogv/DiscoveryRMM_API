using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Agregados horários por canal (avg/min/max/last) — retenção longa dos gráficos.
/// </summary>
[Migration(20270101_261)]
public class M261_CreateSensorRollups : Migration
{
    public override void Up()
    {
        Create.Table("agent_sensor_rollups")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("sensor_id").AsGuid().NotNullable()
            .WithColumn("target_key").AsString(100).NotNullable().WithDefaultValue("")
            .WithColumn("channel").AsString(100).NotNullable()
            .WithColumn("bucket_start").AsDateTimeOffset().NotNullable()
            .WithColumn("avg").AsCustom("numeric(18,4)").Nullable()
            .WithColumn("min").AsCustom("numeric(18,4)").Nullable()
            .WithColumn("max").AsCustom("numeric(18,4)").Nullable()
            .WithColumn("last").AsCustom("numeric(18,4)").Nullable()
            .WithColumn("samples").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("failures").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("worst_status").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("fk_sensor_rollups_agent")
            .FromTable("agent_sensor_rollups").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
        Create.ForeignKey("fk_sensor_rollups_sensor")
            .FromTable("agent_sensor_rollups").ForeignColumn("sensor_id")
            .ToTable("sensor_definitions").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_sensor_rollups_bucket")
            .OnTable("agent_sensor_rollups")
            .OnColumn("agent_id").Ascending()
            .OnColumn("sensor_id").Ascending()
            .OnColumn("target_key").Ascending()
            .OnColumn("channel").Ascending()
            .OnColumn("bucket_start").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_sensor_rollups_bucket_start")
            .OnTable("agent_sensor_rollups")
            .OnColumn("bucket_start").Ascending();
    }

    public override void Down()
    {
        Delete.Table("agent_sensor_rollups");
    }
}
