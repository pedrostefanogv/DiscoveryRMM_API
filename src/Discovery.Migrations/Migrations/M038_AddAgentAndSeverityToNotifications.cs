using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260307_038)]
public class M038_AddAgentAndSeverityToNotifications : Migration
{
    public override void Up()
    {
        Alter.Table("app_notifications")
            .AddColumn("severity").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("recipient_agent_id").AsGuid().Nullable();

        Create.Index("ix_app_notifications_agent_read_created")
            .OnTable("app_notifications")
            .OnColumn("recipient_agent_id")
            .Ascending()
            .OnColumn("is_read")
            .Ascending()
            .OnColumn("created_at")
            .Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_app_notifications_agent_read_created").OnTable("app_notifications");
        Delete.Column("recipient_agent_id").FromTable("app_notifications");
        Delete.Column("severity").FromTable("app_notifications");
    }
}
