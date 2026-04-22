using FluentMigrator;

namespace Discovery.Migrations.Migrations;

[Migration(20260301_008)]
public class M008_CreateWorkflowAndUpdateTickets : Migration
{
    public override void Up()
    {
        // Workflow states — states com client_id NULL são globais (defaults)
        Create.Table("workflow_states")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_workflow_states_client", "clients", "id")
            .WithColumn("name").AsString(100).NotNullable()
            .WithColumn("color").AsString(7).Nullable() // hex color #RRGGBB
            .WithColumn("is_initial").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_final").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("sort_order").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_workflow_states_client_id").OnTable("workflow_states").OnColumn("client_id");

        // Workflow transitions
        Create.Table("workflow_transitions")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("fk_workflow_transitions_client", "clients", "id")
            .WithColumn("from_state_id").AsGuid().NotNullable().ForeignKey("fk_transitions_from", "workflow_states", "id")
            .WithColumn("to_state_id").AsGuid().NotNullable().ForeignKey("fk_transitions_to", "workflow_states", "id")
            .WithColumn("name").AsString(100).Nullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Index("ix_workflow_transitions_client_id").OnTable("workflow_transitions").OnColumn("client_id");
        Create.Index("ix_workflow_transitions_from").OnTable("workflow_transitions").OnColumn("from_state_id");

        // Alterar tabela tickets: remover coluna "status" (int enum) e adicionar "workflow_state_id"
        Alter.Table("tickets")
            .AddColumn("workflow_state_id").AsGuid().Nullable();

        // Remover index antes de dropar a coluna (SQLite exige)
        Delete.Index("ix_tickets_status").OnTable("tickets");

        // Remover a coluna status antiga
        Delete.Column("status").FromTable("tickets");

        // Seed: estados globais default
        // Serão inseridos via código no startup (DatabaseSeeder), não na migration
    }

    public override void Down()
    {
        // Restaurar coluna status
        Alter.Table("tickets")
            .AddColumn("status").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Index("ix_tickets_status").OnTable("tickets").OnColumn("status");

        Delete.Column("workflow_state_id").FromTable("tickets");
        Delete.Table("workflow_transitions");
        Delete.Table("workflow_states");
    }
}
