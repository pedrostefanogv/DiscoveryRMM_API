using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Definições de sensores (monitoramento periódico: estado + canais numéricos).
/// Escopo Global/Cliente/Site/Agente, igual aos AutomationTaskDefinition.
/// Unicidade de "key" é POR ESCOPO e só entre ativas — índice parcial
/// expressional em SQL cru (mesmo padrão da M254).
/// </summary>
[Migration(20270101_259)]
public class M259_CreateSensorDefinitions : Migration
{
    private const string Table = "sensor_definitions";

    public override void Up()
    {
        Create.Table(Table)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("key").AsString(100).Nullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("description").AsString(2000).Nullable()
            .WithColumn("sensor_type").AsInt32().NotNullable().WithDefaultValue(13)
            .WithColumn("scope_type").AsInt32().NotNullable().WithDefaultValue(3)
            .WithColumn("client_id").AsGuid().Nullable()
            .WithColumn("site_id").AsGuid().Nullable()
            .WithColumn("agent_id").AsGuid().Nullable()
            .WithColumn("include_tags_json").AsCustom("jsonb").Nullable()
            .WithColumn("exclude_tags_json").AsCustom("jsonb").Nullable()
            .WithColumn("target").AsString(300).Nullable()
            .WithColumn("warning_threshold").AsCustom("numeric(18,4)").Nullable()
            .WithColumn("critical_threshold").AsCustom("numeric(18,4)").Nullable()
            .WithColumn("threshold_unit").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("comparison").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("options_json").AsCustom("jsonb").Nullable()
            .WithColumn("script_id").AsGuid().Nullable()
            .WithColumn("channels_json").AsCustom("jsonb").Nullable()
            .WithColumn("extractors_json").AsCustom("jsonb").Nullable()
            .WithColumn("exit_code_map_json").AsCustom("jsonb").Nullable()
            .WithColumn("chart_json").AsCustom("jsonb").Nullable()
            .WithColumn("interval_seconds").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("timeout_seconds").AsInt32().NotNullable().WithDefaultValue(60)
            .WithColumn("store_samples").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("fails_before_alert").AsInt32().NotNullable().WithDefaultValue(2)
            .WithColumn("min_severity_to_alert").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("severity_when_failing").AsInt32().NotNullable().WithDefaultValue(2)
            .WithColumn("alert_on_sensor_error").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("display_order").AsInt32().NotNullable().WithDefaultValue(50)
            .WithColumn("is_pinned").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("created_by").AsString(256).Nullable();

        Create.ForeignKey("fk_sensor_definitions_client")
            .FromTable(Table).ForeignColumn("client_id")
            .ToTable("clients").PrimaryColumn("id").OnDelete(System.Data.Rule.SetNull);
        Create.ForeignKey("fk_sensor_definitions_site")
            .FromTable(Table).ForeignColumn("site_id")
            .ToTable("sites").PrimaryColumn("id").OnDelete(System.Data.Rule.SetNull);
        Create.ForeignKey("fk_sensor_definitions_agent")
            .FromTable(Table).ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.SetNull);
        Create.ForeignKey("fk_sensor_definitions_script")
            .FromTable(Table).ForeignColumn("script_id")
            .ToTable("automation_script_definitions").PrimaryColumn("id").OnDelete(System.Data.Rule.SetNull);

        Create.Index("ix_sensor_definitions_scope")
            .OnTable(Table)
            .OnColumn("scope_type").Ascending()
            .OnColumn("client_id").Ascending()
            .OnColumn("site_id").Ascending()
            .OnColumn("agent_id").Ascending();

        Create.Index("ix_sensor_definitions_active_updated")
            .OnTable(Table)
            .OnColumn("is_active").Ascending()
            .OnColumn("updated_at").Ascending();

        Create.Index("ix_sensor_definitions_key").OnTable(Table).OnColumn("key").Ascending();

        // Unicidade por escopo, apenas entre ativas (EF não representa índice parcial).
        Execute.Sql(@"
            CREATE UNIQUE INDEX ux_sensor_definitions_scope_key
            ON sensor_definitions (
                COALESCE(client_id, '00000000-0000-0000-0000-000000000000'::uuid),
                COALESCE(site_id,   '00000000-0000-0000-0000-000000000000'::uuid),
                COALESCE(agent_id,  '00000000-0000-0000-0000-000000000000'::uuid),
                key)
            WHERE key IS NOT NULL AND deleted_at IS NULL;
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS ux_sensor_definitions_scope_key;");
        Delete.Table(Table);
    }
}
