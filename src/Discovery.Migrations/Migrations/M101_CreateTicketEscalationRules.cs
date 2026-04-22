using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_101)]
public class M101_CreateTicketEscalationRules : Migration
{
    public override void Up()
    {
        Create.Table("ticket_escalation_rules")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("workflow_profile_id").AsGuid().NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("trigger_at_sla_percent").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("trigger_at_hours_before").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("reassign_to_user_id").AsGuid().Nullable()
            .WithColumn("reassign_to_department_id").AsGuid().Nullable()
            .WithColumn("bump_priority").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("notify_assignee").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable();

        Create.Index("ix_ticket_escalation_rules_profile_id")
            .OnTable("ticket_escalation_rules")
            .OnColumn("workflow_profile_id");

        Create.Index("ix_ticket_escalation_rules_is_active")
            .OnTable("ticket_escalation_rules")
            .OnColumn("is_active");
    }

    public override void Down()
    {
        Delete.Table("ticket_escalation_rules");
    }
}
