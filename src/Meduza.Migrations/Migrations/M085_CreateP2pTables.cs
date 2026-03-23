using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260323_085)]
public class M085_CreateP2pTables : Migration
{
    public override void Up()
    {
        // ── p2p_agent_telemetry ────────────────────────────────────────────
        Create.Table("p2p_agent_telemetry")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("site_id").AsGuid().NotNullable()
            .WithColumn("client_id").AsGuid().NotNullable()
            .WithColumn("collected_at").AsCustom("timestamptz").NotNullable()
            .WithColumn("received_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            // métricas cumulativas
            .WithColumn("published_artifacts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("replications_started").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("replications_succeeded").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("replications_failed").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("bytes_served").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("bytes_downloaded").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("queued_replications").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("active_replications").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("auto_distribution_runs").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("catalog_refresh_runs").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("chunked_downloads").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("chunks_downloaded").AsInt64().NotNullable().WithDefaultValue(0)
            // seed plan vigente
            .WithColumn("plan_total_agents").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("plan_configured_percent").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("plan_min_seeds").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("plan_selected_seeds").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Index("ix_p2p_telemetry_agent_time")
            .OnTable("p2p_agent_telemetry")
            .OnColumn("agent_id").Ascending()
            .OnColumn("collected_at").Descending();

        Create.Index("ix_p2p_telemetry_site_time")
            .OnTable("p2p_agent_telemetry")
            .OnColumn("site_id").Ascending()
            .OnColumn("collected_at").Descending();

        Create.Index("ix_p2p_telemetry_client_time")
            .OnTable("p2p_agent_telemetry")
            .OnColumn("client_id").Ascending()
            .OnColumn("collected_at").Descending();

        // ── p2p_artifact_presence ──────────────────────────────────────────
        Create.Table("p2p_artifact_presence")
            .WithColumn("artifact_id").AsString(512).NotNullable()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("site_id").AsGuid().NotNullable()
            .WithColumn("client_id").AsGuid().NotNullable()
            .WithColumn("artifact_name").AsString(260).Nullable()
            .WithColumn("id_is_synthetic").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("last_seen_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.PrimaryKey("pk_p2p_artifact_presence")
            .OnTable("p2p_artifact_presence")
            .Columns("artifact_id", "agent_id");

        Create.Index("ix_p2p_presence_artifact_time")
            .OnTable("p2p_artifact_presence")
            .OnColumn("artifact_id").Ascending()
            .OnColumn("last_seen_at").Descending();

        Create.Index("ix_p2p_presence_site_time")
            .OnTable("p2p_artifact_presence")
            .OnColumn("site_id").Ascending()
            .OnColumn("last_seen_at").Descending();

        Create.Index("ix_p2p_presence_client_time")
            .OnTable("p2p_artifact_presence")
            .OnColumn("client_id").Ascending()
            .OnColumn("last_seen_at").Descending();

        // ── p2p_seed_plan ──────────────────────────────────────────────────
        Create.Table("p2p_seed_plan")
            .WithColumn("site_id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().NotNullable()
            .WithColumn("total_agents").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("configured_percent").AsInt32().NotNullable().WithDefaultValue(10)
            .WithColumn("min_seeds").AsInt32().NotNullable().WithDefaultValue(2)
            .WithColumn("selected_seeds").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("generated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_p2p_seed_plan_client")
            .OnTable("p2p_seed_plan")
            .OnColumn("client_id").Ascending();
    }

    public override void Down()
    {
        Delete.Table("p2p_seed_plan");
        Delete.Table("p2p_artifact_presence");
        Delete.Table("p2p_agent_telemetry");
    }
}
