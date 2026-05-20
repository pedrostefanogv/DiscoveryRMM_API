using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260520_131)]
public class M131_AddMachineScore : Migration
{
    public override void Up()
    {
        Alter.Table("agent_hardware_info")
            .AddColumn("machine_score").AsInt32().Nullable();

        Create.Index("ix_hardware_machine_score")
            .OnTable("agent_hardware_info")
            .OnColumn("machine_score");
    }

    public override void Down()
    {
        Delete.Index("ix_hardware_machine_score")
            .OnTable("agent_hardware_info");

        Delete.Column("machine_score")
            .FromTable("agent_hardware_info");
    }
}
