using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260304_018)]
public class M018_AddAgentSoftwarePaginationIndex : Migration
{
    public override void Up()
    {
        if (Schema.Table("agent_software_inventory").Exists()
            && !Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_present_id").Exists())
        {
            Create.Index("ix_agent_software_inventory_agent_present_id")
                .OnTable("agent_software_inventory")
                .OnColumn("agent_id").Ascending()
                .OnColumn("is_present").Ascending()
                .OnColumn("id").Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agent_software_inventory").Exists()
            && Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_present_id").Exists())
        {
            Delete.Index("ix_agent_software_inventory_agent_present_id").OnTable("agent_software_inventory");
        }
    }
}
