using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260415_096)]
public class M096_CreateTicketAlertRules : Migration
{
    public override void Up()
    {
        Create.Table("ticket_alert_rules")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("workflow_state_id").AsGuid().NotNullable()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("message").AsString(2000).NotNullable()
            .WithColumn("alert_type").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("timeout_seconds").AsInt32().Nullable()
            .WithColumn("actions_json").AsCustom("jsonb").Nullable()
            .WithColumn("default_action").AsString(100).Nullable()
            .WithColumn("icon").AsString(50).NotNullable().WithDefaultValue("info")
            .WithColumn("scope_preference").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("is_enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_ticket_alert_rules_workflow_state_id")
            .OnTable("ticket_alert_rules")
            .OnColumn("workflow_state_id");

        Create.Index("ix_ticket_alert_rules_is_enabled")
            .OnTable("ticket_alert_rules")
            .OnColumn("is_enabled");
    }

    public override void Down()
    {
        Delete.Table("ticket_alert_rules");
    }
}
