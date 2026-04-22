using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260301_012)]
public class M012_AddIndexes : Migration
{
    public override void Up()
    {
        // Clients
        Create.Index("ix_clients_active_name")
            .OnTable("clients")
            .OnColumn("is_active")
            .Ascending()
            .OnColumn("name")
            .Ascending();

        // Sites
        Create.Index("ix_sites_client_active_name")
            .OnTable("sites")
            .OnColumn("client_id")
            .Ascending()
            .OnColumn("is_active")
            .Ascending()
            .OnColumn("name")
            .Ascending();

        // Agents
        Create.Index("ix_agents_site_hostname")
            .OnTable("agents")
            .OnColumn("site_id")
            .Ascending()
            .OnColumn("hostname")
            .Ascending();

        // Agent commands
        Create.Index("ix_commands_agent_created_at")
            .OnTable("agent_commands")
            .OnColumn("agent_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        Create.Index("ix_commands_agent_status_created_at")
            .OnTable("agent_commands")
            .OnColumn("agent_id")
            .Ascending()
            .OnColumn("status")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        // Tickets
        Create.Index("ix_tickets_client_created_at")
            .OnTable("tickets")
            .OnColumn("client_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        Create.Index("ix_tickets_client_workflow_created_at")
            .OnTable("tickets")
            .OnColumn("client_id")
            .Ascending()
            .OnColumn("workflow_state_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        Create.Index("ix_tickets_workflow_created_at")
            .OnTable("tickets")
            .OnColumn("workflow_state_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        // Ticket comments
        Create.Index("ix_ticket_comments_ticket_created_at")
            .OnTable("ticket_comments")
            .OnColumn("ticket_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        // Agent tokens
        Create.Index("ix_agent_tokens_agent_created_at")
            .OnTable("agent_tokens")
            .OnColumn("agent_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        // Workflow states
        Create.Index("ix_workflow_states_initial_client")
            .OnTable("workflow_states")
            .OnColumn("is_initial")
            .Ascending()
            .OnColumn("client_id")
            .Ascending();

        // Workflow transitions
        Create.Index("ix_workflow_transitions_from_client")
            .OnTable("workflow_transitions")
            .OnColumn("from_state_id")
            .Ascending()
            .OnColumn("client_id")
            .Ascending();

        Create.Index("ix_workflow_transitions_from_to_client")
            .OnTable("workflow_transitions")
            .OnColumn("from_state_id")
            .Ascending()
            .OnColumn("to_state_id")
            .Ascending()
            .OnColumn("client_id")
            .Ascending();

        // Logs (filter + order by created_at)
        Create.Index("ix_logs_client_created_at")
            .OnTable("logs")
            .OnColumn("client_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        Create.Index("ix_logs_site_created_at")
            .OnTable("logs")
            .OnColumn("site_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();

        Create.Index("ix_logs_agent_created_at")
            .OnTable("logs")
            .OnColumn("agent_id")
            .Ascending()
            .OnColumn("created_at")
            .Ascending();
    }

    public override void Down()
    {
        Delete.Index("ix_logs_agent_created_at").OnTable("logs");
        Delete.Index("ix_logs_site_created_at").OnTable("logs");
        Delete.Index("ix_logs_client_created_at").OnTable("logs");

        Delete.Index("ix_workflow_transitions_from_to_client").OnTable("workflow_transitions");
        Delete.Index("ix_workflow_transitions_from_client").OnTable("workflow_transitions");

        Delete.Index("ix_workflow_states_initial_client").OnTable("workflow_states");

        Delete.Index("ix_agent_tokens_agent_created_at").OnTable("agent_tokens");

        Delete.Index("ix_ticket_comments_ticket_created_at").OnTable("ticket_comments");
        Delete.Index("ix_tickets_workflow_created_at").OnTable("tickets");
        Delete.Index("ix_tickets_client_workflow_created_at").OnTable("tickets");
        Delete.Index("ix_tickets_client_created_at").OnTable("tickets");

        Delete.Index("ix_commands_agent_status_created_at").OnTable("agent_commands");
        Delete.Index("ix_commands_agent_created_at").OnTable("agent_commands");

        Delete.Index("ix_agents_site_hostname").OnTable("agents");

        Delete.Index("ix_sites_client_active_name").OnTable("sites");

        Delete.Index("ix_clients_active_name").OnTable("clients");
    }
}
