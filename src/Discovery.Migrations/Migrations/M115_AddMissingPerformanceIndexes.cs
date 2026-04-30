using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Adiciona índices faltantes identificados em auditoria de performance:
/// - tickets.agent_id: dashboards e listagens de tickets por agente
/// - tickets.site_id: relatórios e filtros por site
/// - tickets.assigned_to_user_id: busca de tickets atribuídos a um técnico
/// - agents.status: listagens de agentes online/offline
/// - agents.last_seen_at: ordenação por último heartbeat
/// </summary>
[Migration(20260429_115, "Add missing performance indexes on tickets and agents")]
public class M115_AddMissingPerformanceIndexes : Migration
{
    public override void Up()
    {
        // ── tickets.agent_id ───────────────────────────────────────────────
        if (!Schema.Table("tickets").Index("ix_tickets_agent_id").Exists())
        {
            Create.Index("ix_tickets_agent_id")
                .OnTable("tickets")
                .OnColumn("agent_id").Ascending()
                .OnColumn("created_at").Descending();
        }

        // ── tickets.site_id ────────────────────────────────────────────────
        if (!Schema.Table("tickets").Index("ix_tickets_site_id").Exists())
        {
            Create.Index("ix_tickets_site_id")
                .OnTable("tickets")
                .OnColumn("site_id").Ascending()
                .OnColumn("created_at").Descending();
        }

        // ── tickets.assigned_to_user_id ────────────────────────────────────
        if (Schema.Table("tickets").Column("assigned_to_user_id").Exists()
            && !Schema.Table("tickets").Index("ix_tickets_assigned_to_user_id").Exists())
        {
            Create.Index("ix_tickets_assigned_to_user_id")
                .OnTable("tickets")
                .OnColumn("assigned_to_user_id").Ascending()
                .OnColumn("created_at").Descending();
        }

        // ── agents.status ──────────────────────────────────────────────────
        if (!Schema.Table("agents").Index("ix_agents_status").Exists())
        {
            Create.Index("ix_agents_status")
                .OnTable("agents")
                .OnColumn("status").Ascending()
                .OnColumn("site_id").Ascending();
        }

        // ── agents.last_seen_at ────────────────────────────────────────────
        if (!Schema.Table("agents").Index("ix_agents_last_seen_at").Exists())
        {
            Create.Index("ix_agents_last_seen_at")
                .OnTable("agents")
                .OnColumn("last_seen_at").Descending();
        }
    }

    public override void Down()
    {
        if (Schema.Table("agents").Index("ix_agents_last_seen_at").Exists())
            Delete.Index("ix_agents_last_seen_at").OnTable("agents");

        if (Schema.Table("agents").Index("ix_agents_status").Exists())
            Delete.Index("ix_agents_status").OnTable("agents");

        if (Schema.Table("tickets").Index("ix_tickets_assigned_to_user_id").Exists())
            Delete.Index("ix_tickets_assigned_to_user_id").OnTable("tickets");

        if (Schema.Table("tickets").Index("ix_tickets_site_id").Exists())
            Delete.Index("ix_tickets_site_id").OnTable("tickets");

        if (Schema.Table("tickets").Index("ix_tickets_agent_id").Exists())
            Delete.Index("ix_tickets_agent_id").OnTable("tickets");
    }
}
