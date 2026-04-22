using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_105)]
public class M105_CreateTicketAutomationLinks : Migration
{
    public override void Up()
    {
        Create.Table("ticket_automation_links")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable()
            .WithColumn("automation_task_definition_id").AsGuid().NotNullable()
            .WithColumn("status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("requested_by").AsString(255).Nullable()
            .WithColumn("reviewed_by").AsString(255).Nullable()
            .WithColumn("note").AsString(2000).Nullable()
            .WithColumn("requested_at").AsDateTimeOffset().NotNullable()
            .WithColumn("reviewed_at").AsDateTimeOffset().Nullable();

        Create.Index("ix_ticket_automation_links_ticket_id")
            .OnTable("ticket_automation_links")
            .OnColumn("ticket_id");
    }

    public override void Down()
    {
        Delete.Table("ticket_automation_links");
    }
}
