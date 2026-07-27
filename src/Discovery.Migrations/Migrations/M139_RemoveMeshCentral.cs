using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Remove integração MeshCentral: dropa tabelas e colunas relacionadas.
/// </summary>
[Migration(20260727_139)]
public class M139_RemoveMeshCentral : Migration
{
    public override void Up()
    {
        // Drop meshcentral_rights_profiles
        if (Schema.Table("meshcentral_rights_profiles").Exists())
        {
            Delete.Table("meshcentral_rights_profiles");
        }

        // Drop ticket_remote_sessions (sera substituido por remote_sessions)
        if (Schema.Table("ticket_remote_sessions").Exists())
        {
            Delete.Table("ticket_remote_sessions");
        }

        // Remove meshcentral columns from agents
        // Note: DROP COLUMN automatically drops any dependent index (ix_agents_meshcentral_node_id)
        if (Schema.Table("agents").Column("meshcentral_node_id").Exists())
        {
            Delete.Column("meshcentral_node_id").FromTable("agents");
        }

        // Remove meshcentral columns from server_configurations
        if (Schema.Table("server_configurations").Column("meshcentral_group_policy_profile").Exists())
        {
            Delete.Column("meshcentral_group_policy_profile").FromTable("server_configurations");
        }

        // Remove meshcentral columns from client_configurations
        if (Schema.Table("client_configurations").Column("meshcentral_group_policy_profile").Exists())
        {
            Delete.Column("meshcentral_group_policy_profile").FromTable("client_configurations");
        }

        // Remove meshcentral columns from site_configurations
        if (Schema.Table("site_configurations").Column("meshcentral_group_policy_profile").Exists())
        {
            Delete.Column("meshcentral_group_policy_profile").FromTable("site_configurations");
        }

        if (Schema.Table("site_configurations").Column("meshcentral_group_name").Exists())
        {
            Delete.Column("meshcentral_group_name").FromTable("site_configurations");
            Delete.Column("meshcentral_mesh_id").FromTable("site_configurations");
            Delete.Column("meshcentral_applied_group_policy_profile").FromTable("site_configurations");
            Delete.Column("meshcentral_applied_group_policy_at").FromTable("site_configurations");
        }
    }

    public override void Down()
    {
        // MeshCentral was intentionally removed — no rollback.
    }
}
