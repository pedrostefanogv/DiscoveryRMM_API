using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Cria tabela p2p_artifact_manifest e adiciona colunas de telemetria enriquecida
/// em p2p_agent_telemetry.
///
/// Parte da otimização P2P (manifesto canônico, host load, peer metrics).
/// </summary>
[Migration(20260513_126)]
public class M126_AddP2pOptimizationTables : Migration
{
    public override void Up()
    {
        // ── p2p_artifact_manifest ──────────────────────────────────────────
        if (!Schema.Table("p2p_artifact_manifest").Exists())
        {
            Create.Table("p2p_artifact_manifest")
                .WithColumn("artifact_id").AsGuid().PrimaryKey()
                .WithColumn("client_id").AsGuid().NotNullable()
                .WithColumn("manifest_json").AsCustom("text").NotNullable()
                .WithColumn("sha256").AsString(64).NotNullable()
                .WithColumn("total_size").AsInt64().NotNullable()
                .WithColumn("chunk_size").AsInt32().NotNullable()
                .WithColumn("total_chunks").AsInt32().NotNullable()
                .WithColumn("generated_by").AsGuid().NotNullable()
                .WithColumn("generated_at").AsCustom("timestamptz").NotNullable()
                .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

            Create.Index("ix_p2p_manifest_client")
                .OnTable("p2p_artifact_manifest")
                .OnColumn("client_id").Ascending();
        }

        // ── Novas colunas em p2p_agent_telemetry ───────────────────────────
        // Host load (opcionais)
        if (!Schema.Table("p2p_agent_telemetry").Column("host_cpu_percent").Exists())
        {
            Alter.Table("p2p_agent_telemetry")
                .AddColumn("host_cpu_percent").AsDouble().Nullable();
        }

        if (!Schema.Table("p2p_agent_telemetry").Column("host_memory_percent").Exists())
        {
            Alter.Table("p2p_agent_telemetry")
                .AddColumn("host_memory_percent").AsDouble().Nullable();
        }

        if (!Schema.Table("p2p_agent_telemetry").Column("host_disk_busy_percent").Exists())
        {
            Alter.Table("p2p_agent_telemetry")
                .AddColumn("host_disk_busy_percent").AsDouble().Nullable();
        }

        if (!Schema.Table("p2p_agent_telemetry").Column("host_cpu_cores").Exists())
        {
            Alter.Table("p2p_agent_telemetry")
                .AddColumn("host_cpu_cores").AsInt32().NotNullable().WithDefaultValue(0);
        }

        if (!Schema.Table("p2p_agent_telemetry").Column("host_ram_gb").Exists())
        {
            Alter.Table("p2p_agent_telemetry")
                .AddColumn("host_ram_gb").AsDouble().NotNullable().WithDefaultValue(0);
        }

        // Peer metrics
        if (!Schema.Table("p2p_agent_telemetry").Column("known_peers").Exists())
        {
            Alter.Table("p2p_agent_telemetry")
                .AddColumn("known_peers").AsInt32().NotNullable().WithDefaultValue(0);
        }

        if (!Schema.Table("p2p_agent_telemetry").Column("connected_peers").Exists())
        {
            Alter.Table("p2p_agent_telemetry")
                .AddColumn("connected_peers").AsInt32().NotNullable().WithDefaultValue(0);
        }
    }

    public override void Down()
    {
        // Remove colunas de telemetria enriquecida
        if (Schema.Table("p2p_agent_telemetry").Column("connected_peers").Exists())
            Delete.Column("connected_peers").FromTable("p2p_agent_telemetry");

        if (Schema.Table("p2p_agent_telemetry").Column("known_peers").Exists())
            Delete.Column("known_peers").FromTable("p2p_agent_telemetry");

        if (Schema.Table("p2p_agent_telemetry").Column("host_ram_gb").Exists())
            Delete.Column("host_ram_gb").FromTable("p2p_agent_telemetry");

        if (Schema.Table("p2p_agent_telemetry").Column("host_cpu_cores").Exists())
            Delete.Column("host_cpu_cores").FromTable("p2p_agent_telemetry");

        if (Schema.Table("p2p_agent_telemetry").Column("host_disk_busy_percent").Exists())
            Delete.Column("host_disk_busy_percent").FromTable("p2p_agent_telemetry");

        if (Schema.Table("p2p_agent_telemetry").Column("host_memory_percent").Exists())
            Delete.Column("host_memory_percent").FromTable("p2p_agent_telemetry");

        if (Schema.Table("p2p_agent_telemetry").Column("host_cpu_percent").Exists())
            Delete.Column("host_cpu_percent").FromTable("p2p_agent_telemetry");

        // Remove tabela p2p_artifact_manifest
        if (Schema.Table("p2p_artifact_manifest").Exists())
        {
            Delete.Index("ix_p2p_manifest_client").OnTable("p2p_artifact_manifest");
            Delete.Table("p2p_artifact_manifest");
        }
    }
}
