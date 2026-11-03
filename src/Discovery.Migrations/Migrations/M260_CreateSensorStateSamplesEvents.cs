using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Estado corrente (1 linha por agente+sensor+alvo), amostras brutas (série
/// curta para gráficos) e timeline de eventos/transições.
/// </summary>
[Migration(20270101_260)]
public class M260_CreateSensorStateSamplesEvents : Migration
{
    public override void Up()
    {
        Create.Table("agent_sensor_states")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("sensor_id").AsGuid().NotNullable()
            .WithColumn("target_key").AsString(100).NotNullable().WithDefaultValue("")
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("severity").AsInt32().Nullable()
            .WithColumn("message").AsString(1000).Nullable()
            .WithColumn("channels_json").AsCustom("jsonb").Nullable()
            .WithColumn("primary_channel").AsString(100).Nullable()
            .WithColumn("primary_value").AsCustom("numeric(18,4)").Nullable()
            .WithColumn("exit_code").AsInt32().Nullable()
            .WithColumn("duration_ms").AsInt32().Nullable()
            .WithColumn("output_excerpt").AsCustom("text").Nullable()
            .WithColumn("consecutive_failures").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("total_runs").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("total_failures").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("last_run_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_status_changed_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_alerted_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_alert_severity").AsInt32().Nullable()
            .WithColumn("sparkline_json").AsCustom("jsonb").Nullable()
            .WithColumn("last_run_id").AsGuid().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.ForeignKey("fk_sensor_states_agent")
            .FromTable("agent_sensor_states").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
        Create.ForeignKey("fk_sensor_states_sensor")
            .FromTable("agent_sensor_states").ForeignColumn("sensor_id")
            .ToTable("sensor_definitions").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_agent_sensor_states_agent_sensor_target")
            .OnTable("agent_sensor_states")
            .OnColumn("agent_id").Ascending()
            .OnColumn("sensor_id").Ascending()
            .OnColumn("target_key").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_agent_sensor_states_agent_status")
            .OnTable("agent_sensor_states")
            .OnColumn("agent_id").Ascending()
            .OnColumn("status").Ascending();

        Create.Table("agent_sensor_samples")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("sensor_id").AsGuid().NotNullable()
            .WithColumn("target_key").AsString(100).NotNullable().WithDefaultValue("")
            .WithColumn("collected_at").AsDateTimeOffset().NotNullable()
            .WithColumn("captured_at").AsDateTimeOffset().NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("severity").AsInt32().Nullable()
            .WithColumn("channels_json").AsCustom("jsonb").Nullable()
            .WithColumn("message").AsString(500).Nullable()
            .WithColumn("run_id").AsGuid().NotNullable()
            .WithColumn("exit_code").AsInt32().Nullable();

        Create.ForeignKey("fk_sensor_samples_agent")
            .FromTable("agent_sensor_samples").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
        Create.ForeignKey("fk_sensor_samples_sensor")
            .FromTable("agent_sensor_samples").ForeignColumn("sensor_id")
            .ToTable("sensor_definitions").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_sensor_samples_agent_sensor_target_captured")
            .OnTable("agent_sensor_samples")
            .OnColumn("agent_id").Ascending()
            .OnColumn("sensor_id").Ascending()
            .OnColumn("target_key").Ascending()
            .OnColumn("captured_at").Descending();

        Create.Index("ux_sensor_samples_run")
            .OnTable("agent_sensor_samples")
            .OnColumn("agent_id").Ascending()
            .OnColumn("sensor_id").Ascending()
            .OnColumn("target_key").Ascending()
            .OnColumn("run_id").Ascending()
            .WithOptions().Unique();

        // BRIN na coluna de tempo: barato para inserção em série temporal e
        // suficiente para as consultas por faixa (gráfico).
        Execute.Sql(@"CREATE INDEX ix_sensor_samples_captured_brin
                      ON agent_sensor_samples USING brin (captured_at);");

        Create.Table("agent_sensor_events")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("sensor_id").AsGuid().NotNullable()
            .WithColumn("target_key").AsString(100).NotNullable().WithDefaultValue("")
            .WithColumn("run_id").AsGuid().NotNullable()
            .WithColumn("kind").AsInt32().NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("severity").AsInt32().Nullable()
            .WithColumn("previous_status").AsInt32().Nullable()
            .WithColumn("previous_severity").AsInt32().Nullable()
            .WithColumn("message").AsString(1000).Nullable()
            .WithColumn("channels_json").AsCustom("jsonb").Nullable()
            .WithColumn("exit_code").AsInt32().Nullable()
            .WithColumn("occurred_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.ForeignKey("fk_sensor_events_agent")
            .FromTable("agent_sensor_events").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
        Create.ForeignKey("fk_sensor_events_sensor")
            .FromTable("agent_sensor_events").ForeignColumn("sensor_id")
            .ToTable("sensor_definitions").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_sensor_events_agent_sensor_occurred")
            .OnTable("agent_sensor_events")
            .OnColumn("agent_id").Ascending()
            .OnColumn("sensor_id").Ascending()
            .OnColumn("occurred_at").Descending();

        Create.Index("ux_sensor_events_agent_run_kind")
            .OnTable("agent_sensor_events")
            .OnColumn("agent_id").Ascending()
            .OnColumn("run_id").Ascending()
            .OnColumn("kind").Ascending()
            .WithOptions().Unique();
    }

    public override void Down()
    {
        Delete.Table("agent_sensor_events");
        Delete.Table("agent_sensor_samples");
        Delete.Table("agent_sensor_states");
    }
}
