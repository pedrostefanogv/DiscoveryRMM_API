using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20261123_297)]
public class M297_TicketSavedViewsClientAndIndexes : Migration
{
    public override void Up()
    {
        if (Schema.Table("ticket_saved_views").Exists())
        {
            if (!Schema.Table("ticket_saved_views").Column("client_id").Exists())
            {
                Alter.Table("ticket_saved_views")
                    .AddColumn("client_id").AsGuid().Nullable();
            }

            if (!Schema.Table("ticket_saved_views").Index("ix_ticket_saved_views_client_id").Exists())
            {
                Create.Index("ix_ticket_saved_views_client_id")
                    .OnTable("ticket_saved_views")
                    .OnColumn("client_id").Ascending();
            }
        }

        if (Schema.Table("tickets").Exists())
        {
            // Índices compostos/parciais PostgreSQL para listagem, filtros de fila e SLA.
            IfDatabase("Postgres").Execute.Sql("""
                CREATE INDEX IF NOT EXISTS ix_tickets_active_created_id
                    ON tickets (created_at DESC, id DESC)
                    WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS ix_tickets_client_closed
                    ON tickets (client_id, closed_at)
                    WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS ix_tickets_department_closed
                    ON tickets (department_id, closed_at)
                    WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS ix_tickets_assignee_closed
                    ON tickets (assigned_to_user_id, closed_at)
                    WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS ix_tickets_workflow_state
                    ON tickets (workflow_state_id)
                    WHERE deleted_at IS NULL;
                CREATE INDEX IF NOT EXISTS ix_tickets_open_sla_expires
                    ON tickets (sla_expires_at)
                    WHERE deleted_at IS NULL AND closed_at IS NULL;
                CREATE INDEX IF NOT EXISTS ix_tickets_agent_id
                    ON tickets (agent_id)
                    WHERE agent_id IS NOT NULL;
                """);
        }
    }

    public override void Down()
    {
        if (Schema.Table("tickets").Exists())
        {
            IfDatabase("Postgres").Execute.Sql("""
                DROP INDEX IF EXISTS ix_tickets_agent_id;
                DROP INDEX IF EXISTS ix_tickets_open_sla_expires;
                DROP INDEX IF EXISTS ix_tickets_workflow_state;
                DROP INDEX IF EXISTS ix_tickets_assignee_closed;
                DROP INDEX IF EXISTS ix_tickets_department_closed;
                DROP INDEX IF EXISTS ix_tickets_client_closed;
                DROP INDEX IF EXISTS ix_tickets_active_created_id;
                """);
        }

        if (Schema.Table("ticket_saved_views").Exists())
        {
            if (Schema.Table("ticket_saved_views").Index("ix_ticket_saved_views_client_id").Exists())
            {
                Delete.Index("ix_ticket_saved_views_client_id").OnTable("ticket_saved_views");
            }

            if (Schema.Table("ticket_saved_views").Column("client_id").Exists())
            {
                Delete.Column("client_id").FromTable("ticket_saved_views");
            }
        }
    }
}
