using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260301_010)]
public class M010_AddInventoryRaw : Migration
{
    public override void Up()
    {
        Alter.Table("agent_hardware_info")
            .AddColumn("inventory_raw").AsCustom("jsonb").Nullable()
            .AddColumn("inventory_schema_version").AsString(50).Nullable()
            .AddColumn("inventory_collected_at").AsCustom("timestamptz").Nullable();
    }

    public override void Down()
    {
        Delete.Column("inventory_raw").FromTable("agent_hardware_info");
        Delete.Column("inventory_schema_version").FromTable("agent_hardware_info");
        Delete.Column("inventory_collected_at").FromTable("agent_hardware_info");
    }
}
