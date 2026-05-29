using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260528_120)]
public class M120_AddReportJoinPerformanceIndexes : Migration
{
    // Summary of indexes created:
    // ── agent_labels ──
    // ix_agent_labels_agent_id            → Filter/join by agent
    // ix_agent_labels_label               → Filter by label name
    // ── agent_label_rule_matches ──
    // ix_agent_label_rule_matches_rule_id → Count matches per rule
    // ix_agent_label_rule_matches_agent_id→ Find agents by rule match
    // ── automation_execution_reports ──
    // ix_automation_exec_reports_agent_id → Filter/join by agent
    // ix_automation_exec_reports_status   → Filter by execution status
    // ix_automation_exec_reports_created  → Order by creation date
    // ── agent_software_inventory ──
    // ix_agent_software_inv_last_seen     → Order by last seen (reports)
    // ── logs ──
    // ix_logs_site_agent_created          → Composite filter for site+agent reports

    public override void Up()
    {
        // ── agent_labels ──
        if (!Schema.Table("agent_labels").Index("ix_agent_labels_agent_id").Exists())
        {
            Create.Index("ix_agent_labels_agent_id")
                .OnTable("agent_labels")
                .OnColumn("agent_id");
        }

        if (!Schema.Table("agent_labels").Index("ix_agent_labels_label").Exists())
        {
            Create.Index("ix_agent_labels_label")
                .OnTable("agent_labels")
                .OnColumn("label");
        }

        // ── agent_label_rule_matches ──
        if (!Schema.Table("agent_label_rule_matches").Index("ix_agent_label_rule_matches_rule_id").Exists())
        {
            Create.Index("ix_agent_label_rule_matches_rule_id")
                .OnTable("agent_label_rule_matches")
                .OnColumn("rule_id");
        }

        if (!Schema.Table("agent_label_rule_matches").Index("ix_agent_label_rule_matches_agent_id").Exists())
        {
            Create.Index("ix_agent_label_rule_matches_agent_id")
                .OnTable("agent_label_rule_matches")
                .OnColumn("agent_id");
        }

        // ── automation_execution_reports ──
        if (!Schema.Table("automation_execution_reports").Index("ix_automation_exec_reports_agent_id").Exists())
        {
            Create.Index("ix_automation_exec_reports_agent_id")
                .OnTable("automation_execution_reports")
                .OnColumn("agent_id");
        }

        if (!Schema.Table("automation_execution_reports").Index("ix_automation_exec_reports_status").Exists())
        {
            Create.Index("ix_automation_exec_reports_status")
                .OnTable("automation_execution_reports")
                .OnColumn("status");
        }

        if (!Schema.Table("automation_execution_reports").Index("ix_automation_exec_reports_created").Exists())
        {
            Create.Index("ix_automation_exec_reports_created")
                .OnTable("automation_execution_reports")
                .OnColumn("created_at").Descending();
        }

        // ── agent_software_inventory ──
        if (!Schema.Table("agent_software_inventory").Index("ix_agent_software_inv_last_seen").Exists())
        {
            Create.Index("ix_agent_software_inv_last_seen")
                .OnTable("agent_software_inventory")
                .OnColumn("last_seen_at").Descending();
        }

        // ── logs ──
        if (!Schema.Table("logs").Index("ix_logs_site_agent_created").Exists())
        {
            Create.Index("ix_logs_site_agent_created")
                .OnTable("logs")
                .OnColumn("site_id").Ascending()
                .OnColumn("agent_id").Ascending()
                .OnColumn("created_at").Descending();
        }
    }

    public override void Down()
    {
        Delete.Index("ix_agent_labels_agent_id").OnTable("agent_labels");
        Delete.Index("ix_agent_labels_label").OnTable("agent_labels");
        Delete.Index("ix_agent_label_rule_matches_rule_id").OnTable("agent_label_rule_matches");
        Delete.Index("ix_agent_label_rule_matches_agent_id").OnTable("agent_label_rule_matches");
        Delete.Index("ix_automation_exec_reports_agent_id").OnTable("automation_execution_reports");
        Delete.Index("ix_automation_exec_reports_status").OnTable("automation_execution_reports");
        Delete.Index("ix_automation_exec_reports_created").OnTable("automation_execution_reports");
        Delete.Index("ix_agent_software_inv_last_seen").OnTable("agent_software_inventory");
        Delete.Index("ix_logs_site_agent_created").OnTable("logs");
    }
}