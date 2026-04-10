using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260303_017)]
public class M017_MoveSoftwareVersionToAgentInventory : Migration
{
    public override void Up()
    {
        if (Schema.Table("agent_software_inventory").Exists()
            && !Schema.Table("agent_software_inventory").Column("version").Exists())
        {
            Alter.Table("agent_software_inventory").AddColumn("version").AsString(120).Nullable();
        }

        if (Schema.Table("agent_software_inventory").Exists()
            && Schema.Table("software_catalog").Exists()
            && Schema.Table("software_catalog").Column("version").Exists())
        {
            Execute.Sql(
                """
                UPDATE agent_software_inventory inv
                SET version = s.version
                FROM software_catalog s
                WHERE inv.software_id = s.id
                  AND (inv.version IS NULL OR inv.version = '')
                """);
        }
    }

    public override void Down()
    {
        if (Schema.Table("agent_software_inventory").Exists()
            && Schema.Table("agent_software_inventory").Column("version").Exists())
        {
            Delete.Column("version").FromTable("agent_software_inventory");
        }
    }
}
