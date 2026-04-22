using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260417_099)]
public class M099_AddTicketSlaEnhancements : Migration
{
    public override void Up()
    {
        // --- Ticket: FRT fields ---
        Alter.Table("tickets")
            .AddColumn("sla_first_response_expires_at").AsDateTimeOffset().Nullable()
            .AddColumn("first_responded_at").AsDateTimeOffset().Nullable()
            .AddColumn("sla_paused_seconds").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("sla_hold_started_at").AsDateTimeOffset().Nullable();

        // --- WorkflowProfile: first_response_sla_hours ---
        Alter.Table("workflow_profiles")
            .AddColumn("first_response_sla_hours").AsInt32().NotNullable().WithDefaultValue(4);
    }

    public override void Down()
    {
        Delete.Column("sla_first_response_expires_at").FromTable("tickets");
        Delete.Column("first_responded_at").FromTable("tickets");
        Delete.Column("sla_paused_seconds").FromTable("tickets");
        Delete.Column("sla_hold_started_at").FromTable("tickets");
        Delete.Column("first_response_sla_hours").FromTable("workflow_profiles");
    }
}
