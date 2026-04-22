using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_100)]
public class M100_AddWorkflowStatePausesSla : Migration
{
    public override void Up()
    {
        Alter.Table("workflow_states")
            .AddColumn("pauses_sla").AsBoolean().NotNullable().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("pauses_sla").FromTable("workflow_states");
    }
}
