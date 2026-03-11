using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(20260311_050)]
public class M050_AddHardwareComponentsJson : Migration
{
    public override void Up()
    {
        Alter.Table("agent_hardware_info")
            .AddColumn("hardware_components_json").AsCustom("jsonb").Nullable();
    }

    public override void Down()
    {
        Delete.Column("hardware_components_json").FromTable("agent_hardware_info");
    }
}
