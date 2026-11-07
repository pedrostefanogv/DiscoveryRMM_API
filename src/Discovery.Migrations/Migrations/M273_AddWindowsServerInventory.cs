using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Inventário tipado de Windows Server: roles instaladas, RAID/BMC, Hyper-V,
/// cluster de failover e Storage/RDS.
///
/// Contexto: o agente Go já enviava esses blocos (osRoles, serverHardware,
/// hyperV, cluster, storageRds) e a API já os gravava — mas apenas dentro do
/// JSON <c>agent_hardware_info.hardware_components_json</c>, o que os tornava
/// inúteis para consulta/filtro no site. Estas tabelas dão colunas tipadas a
/// esses dados, seguindo o mesmo desenho das demais tabelas filhas de agents.
///
/// Todas as tabelas são filhas de <c>agents</c> (FK com ON DELETE CASCADE):
/// o inventário de servidor não existe sem o agente. A coleta é totalmente
/// SUBSTITUTIVA por agente (o parser monta a lista inteira a cada sync), então
/// as tabelas são tratadas como snapshot corrente, não histórico.
///
/// ATENÇÃO: esta migration apenas DEFINE o schema (aplicada pelo MigrateUp no
/// boot da API). Nada de ALTER destrutivo.
/// </summary>
[Migration(20270108_273)]
public class M273_AddWindowsServerInventory : Migration
{
    public override void Up()
    {
        // ── Roles/features instaladas (ex.: AD DS, DNS, Hyper-V) ──────────
        Create.Table("agent_windows_roles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("display_name").AsString(300).Nullable()
            .WithColumn("installed").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("install_state").AsString(50).Nullable()
            .WithColumn("collected_at").AsDateTimeOffset().NotNullable();

        Create.ForeignKey("fk_agent_windows_roles_agent")
            .FromTable("agent_windows_roles").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_agent_windows_roles_agent_id")
            .OnTable("agent_windows_roles")
            .OnColumn("agent_id").Ascending();

        // ── RAID/BMC (ferramentas de storage detectadas) ──────────────────
        Create.Table("agent_raid_controllers")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("vendor").AsString(200).Nullable()
            .WithColumn("path").AsString(500).Nullable()
            .WithColumn("ok").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("error").AsString(1000).Nullable()
            .WithColumn("controllers").AsInt32().Nullable()
            .WithColumn("virtual_disks").AsInt32().Nullable()
            .WithColumn("physical_disks").AsInt32().Nullable()
            .WithColumn("degraded_json").AsCustom("jsonb").Nullable()
            .WithColumn("collected_at").AsDateTimeOffset().NotNullable();

        Create.ForeignKey("fk_agent_raid_controllers_agent")
            .FromTable("agent_raid_controllers").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_agent_raid_controllers_agent_id")
            .OnTable("agent_raid_controllers")
            .OnColumn("agent_id").Ascending();

        // ── Máquinas virtuais do Hyper-V ──────────────────────────────────
        Create.Table("agent_virtual_machines")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("state").AsString(50).Nullable()
            .WithColumn("state_code").AsInt32().Nullable()
            .WithColumn("memory_assigned_mb").AsInt64().Nullable()
            .WithColumn("uptime_seconds").AsInt64().Nullable()
            .WithColumn("generation").AsInt32().Nullable()
            .WithColumn("integration_services_version").AsString(100).Nullable()
            .WithColumn("collected_at").AsDateTimeOffset().NotNullable();

        Create.ForeignKey("fk_agent_virtual_machines_agent")
            .FromTable("agent_virtual_machines").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_agent_virtual_machines_agent_id")
            .OnTable("agent_virtual_machines")
            .OnColumn("agent_id").Ascending();

        // ── Cluster de failover ───────────────────────────────────────────
        // Nós e recursos ficam em jsonb: listas aninhadas de vida curta, sem
        // consulta relacional própria (mesmo critério de degraded_json).
        Create.Table("agent_clusters")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("nodes_json").AsCustom("jsonb").Nullable()
            .WithColumn("resources_json").AsCustom("jsonb").Nullable()
            .WithColumn("quorum_type").AsString(100).Nullable()
            .WithColumn("quorum_resource").AsString(300).Nullable()
            .WithColumn("degraded_resources").AsInt32().Nullable()
            .WithColumn("collected_at").AsDateTimeOffset().NotNullable();

        Create.ForeignKey("fk_agent_clusters_agent")
            .FromTable("agent_clusters").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_agent_clusters_agent_id")
            .OnTable("agent_clusters")
            .OnColumn("agent_id").Ascending();

        // ── Storage pools (Storage Spaces) ────────────────────────────────
        Create.Table("agent_storage_pools")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("health_status").AsString(50).Nullable()
            .WithColumn("operational_status").AsString(100).Nullable()
            .WithColumn("size_gb").AsInt64().Nullable()
            .WithColumn("allocated_gb").AsInt64().Nullable()
            .WithColumn("disk_count").AsInt32().Nullable()
            .WithColumn("is_primordial").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("collected_at").AsDateTimeOffset().NotNullable();

        Create.ForeignKey("fk_agent_storage_pools_agent")
            .FromTable("agent_storage_pools").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_agent_storage_pools_agent_id")
            .OnTable("agent_storage_pools")
            .OnColumn("agent_id").Ascending();

        // ── Sessões de Remote Desktop Services ────────────────────────────
        Create.Table("agent_rds_sessions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("agent_id").AsGuid().NotNullable()
            .WithColumn("session_id").AsInt32().NotNullable()
            .WithColumn("username").AsString(200).Nullable()
            .WithColumn("state").AsString(50).Nullable()
            .WithColumn("session_type").AsString(50).Nullable()
            .WithColumn("collected_at").AsDateTimeOffset().NotNullable();

        Create.ForeignKey("fk_agent_rds_sessions_agent")
            .FromTable("agent_rds_sessions").ForeignColumn("agent_id")
            .ToTable("agents").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);

        Create.Index("ix_agent_rds_sessions_agent_id")
            .OnTable("agent_rds_sessions")
            .OnColumn("agent_id").Ascending();
    }

    public override void Down()
    {
        // Ordem inversa da criação (as FKs apontam para agents, não entre si).
        Delete.Table("agent_rds_sessions");
        Delete.Table("agent_storage_pools");
        Delete.Table("agent_clusters");
        Delete.Table("agent_virtual_machines");
        Delete.Table("agent_raid_controllers");
        Delete.Table("agent_windows_roles");
    }
}
