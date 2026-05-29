using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260529_130)]
public class M130_AddTicketReportPerformanceIndex : Migration
{
    // ix_tickets_agent_created_at — Composite index for tickets-by-device report queries
    // Used by: ReportDatasetQueryService.QueryTicketsAsync (joins with agents/sites)

    public override void Up()
    {
        if (!Schema.Table("tickets").Index("ix_tickets_agent_created_at").Exists())
        {
            Create.Index("ix_tickets_agent_created_at")
                .OnTable("tickets")
                .OnColumn("agent_id").Ascending()
                .OnColumn("created_at").Descending();
        }
    }

    public override void Down()
    {
        Delete.Index("ix_tickets_agent_created_at").OnTable("tickets");
    }
}
