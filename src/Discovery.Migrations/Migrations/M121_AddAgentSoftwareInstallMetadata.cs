using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260506_121)]
public class M121_AddAgentSoftwareInstallMetadata : Migration
{
    public override void Up()
    {
        if (Schema.Table("agent_software_inventory").Exists()
            && !Schema.Table("agent_software_inventory").Column("install_date").Exists())
        {
            Alter.Table("agent_software_inventory")
                .AddColumn("install_date").AsCustom("timestamptz").Nullable();
        }

        if (Schema.Table("agent_software_inventory").Exists()
            && !Schema.Table("agent_software_inventory").Column("install_source").Exists())
        {
            Alter.Table("agent_software_inventory")
                .AddColumn("install_source").AsString(2000).Nullable();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agent_software_inventory").Exists()
            && Schema.Table("agent_software_inventory").Column("install_source").Exists())
        {
            Delete.Column("install_source").FromTable("agent_software_inventory");
        }

        if (Schema.Table("agent_software_inventory").Exists()
            && Schema.Table("agent_software_inventory").Column("install_date").Exists())
        {
            Delete.Column("install_date").FromTable("agent_software_inventory");
        }
    }
}
