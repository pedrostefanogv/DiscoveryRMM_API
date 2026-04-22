using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260315_066)]
public class M066_AddSoftDeleteToAutomationTasks : Migration
{
    public override void Up()
    {
        Alter.Table("automation_task_definitions")
            .AddColumn("deleted_at").AsCustom("timestamptz").Nullable();

        Create.Index("ix_automation_tasks_deleted_at")
            .OnTable("automation_task_definitions")
            .OnColumn("deleted_at").Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_automation_tasks_deleted_at")
            .OnTable("automation_task_definitions");

        Delete.Column("deleted_at")
            .FromTable("automation_task_definitions");
    }
}
