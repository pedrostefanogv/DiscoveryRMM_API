using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260416_097)]
public class M097_AddMeshCentralNodeIdToAgents : Migration
{
    public override void Up()
    {
        if (!Schema.Table("agents").Column("meshcentral_node_id").Exists())
        {
            Alter.Table("agents")
                .AddColumn("meshcentral_node_id").AsString(200).Nullable();
        }

        if (!Schema.Table("agents").Index("ix_agents_meshcentral_node_id").Exists())
        {
            Create.Index("ix_agents_meshcentral_node_id")
                .OnTable("agents")
                .OnColumn("meshcentral_node_id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Index("ix_agents_meshcentral_node_id").Exists())
        {
            Delete.Index("ix_agents_meshcentral_node_id").OnTable("agents");
        }

        if (Schema.Table("agents").Column("meshcentral_node_id").Exists())
        {
            Delete.Column("meshcentral_node_id").FromTable("agents");
        }
    }
}