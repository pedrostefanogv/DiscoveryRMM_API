using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260303_016)]
public class M016_CreateSoftwareInventory : Migration
{
    public override void Up()
    {
        if (!Schema.Table("software_catalog").Exists())
        {
            Create.Table("software_catalog")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("name").AsString(300).NotNullable()
                .WithColumn("version").AsString(120).Nullable()
                .WithColumn("publisher").AsString(300).Nullable()
                .WithColumn("install_id").AsString(1000).Nullable()
                .WithColumn("serial").AsString(1000).Nullable()
                .WithColumn("source").AsString(120).Nullable()
                .WithColumn("fingerprint").AsString(64).NotNullable().Unique()
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("updated_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        }

        if (!Schema.Table("agent_software_inventory").Exists())
        {
            Create.Table("agent_software_inventory")
                .WithColumn("id").AsGuid().PrimaryKey()
                .WithColumn("agent_id").AsGuid().NotNullable().ForeignKey("fk_agent_software_inventory_agent", "agents", "id")
                .WithColumn("software_id").AsGuid().NotNullable().ForeignKey("fk_agent_software_inventory_software", "software_catalog", "id")
                .WithColumn("collected_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("first_seen_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("last_seen_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("version").AsString(120).Nullable()
                .WithColumn("is_present").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
                .WithColumn("updated_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        }

        if (!Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_software").Exists())
        {
            Create.Index("ix_agent_software_inventory_agent_software")
                .OnTable("agent_software_inventory")
                .OnColumn("agent_id")
                .Ascending()
                .OnColumn("software_id")
                .Ascending();
        }

        if (!Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_present").Exists())
        {
            Create.Index("ix_agent_software_inventory_agent_present")
                .OnTable("agent_software_inventory")
                .OnColumn("agent_id")
                .Ascending()
                .OnColumn("is_present")
                .Ascending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agent_software_inventory").Exists())
        {
            if (Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_present").Exists())
            {
                Delete.Index("ix_agent_software_inventory_agent_present").OnTable("agent_software_inventory");
            }

            if (Schema.Table("agent_software_inventory").Index("ix_agent_software_inventory_agent_software").Exists())
            {
                Delete.Index("ix_agent_software_inventory_agent_software").OnTable("agent_software_inventory");
            }

            Delete.Table("agent_software_inventory");
        }

        if (Schema.Table("software_catalog").Exists())
        {
            Delete.Table("software_catalog");
        }
    }
}
