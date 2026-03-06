using FluentMigrator;

namespace Meduza.Migrations.Migrations;

[Migration(30)]
public class M030_AddDepartmentsWorkflowProfilesAndActivityLogs : Migration
{
    public override void Up()
    {
        // Tabela de Departamentos
        Create.Table("departments")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id")
            .WithColumn("name").AsString(255).NotNullable()
            .WithColumn("description").AsString(1000).Nullable()
            .WithColumn("inherit_from_global_id").AsGuid().Nullable().ForeignKey("departments", "id")
            .WithColumn("sort_order").AsInt32().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().WithDefaultValue(true)
            .WithColumn("created_at").AsDateTime().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTime().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.UniqueConstraint("uc_departments_client_name")
            .OnTable("departments")
            .Columns("client_id", "name");

        Create.Index("idx_departments_client_id")
            .OnTable("departments")
            .OnColumn("client_id");

        Create.Index("idx_departments_is_active")
            .OnTable("departments")
            .OnColumn("is_active");

        // Tabela de Perfis de Workflow
        Create.Table("workflow_profiles")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("client_id").AsGuid().Nullable().ForeignKey("clients", "id")
            .WithColumn("department_id").AsGuid().NotNullable().ForeignKey("departments", "id")
            .WithColumn("name").AsString(255).NotNullable()
            .WithColumn("description").AsString(1000).Nullable()
            .WithColumn("sla_hours").AsInt32().NotNullable().WithDefaultValue(24)
            .WithColumn("default_priority").AsString(50).WithDefaultValue("Medium")
            .WithColumn("is_active").AsBoolean().WithDefaultValue(true)
            .WithColumn("created_at").AsDateTime().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTime().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.UniqueConstraint("uc_workflow_profiles_dept_name")
            .OnTable("workflow_profiles")
            .Columns("client_id", "department_id", "name");

        Create.Index("idx_workflow_profiles_dept")
            .OnTable("workflow_profiles")
            .OnColumn("department_id");

        Create.Index("idx_workflow_profiles_client")
            .OnTable("workflow_profiles")
            .OnColumn("client_id");

        // Tabela de Log de Atividades de Ticket
        Create.Table("ticket_activity_logs")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("ticket_id").AsGuid().NotNullable().ForeignKey("tickets", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("activity_type").AsString(50).NotNullable()
            .WithColumn("changed_by_user_id").AsGuid().Nullable()
            .WithColumn("old_value").AsString(1000).Nullable()
            .WithColumn("new_value").AsString(1000).Nullable()
            .WithColumn("comment").AsString(2000).Nullable()
            .WithColumn("created_at").AsDateTime().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.Index("idx_ticket_activity_logs_ticket")
            .OnTable("ticket_activity_logs")
            .OnColumn("ticket_id");

        Create.Index("idx_ticket_activity_logs_created")
            .OnTable("ticket_activity_logs")
            .OnColumn("created_at")
            .Descending();

        Create.Index("idx_ticket_activity_logs_type")
            .OnTable("ticket_activity_logs")
            .OnColumn("activity_type");

        // Adicionar colunas novas à tabela de Tickets
        Alter.Table("tickets")
            .AddColumn("department_id").AsGuid().Nullable().ForeignKey("departments", "id")
            .AddColumn("workflow_profile_id").AsGuid().Nullable().ForeignKey("workflow_profiles", "id")
            .AddColumn("assigned_to_user_id").AsGuid().Nullable()
            .AddColumn("sla_expires_at").AsDateTime().Nullable()
            .AddColumn("sla_breached").AsBoolean().WithDefaultValue(false);

        // Remover a coluna old assigned_to (string) se existir (será substituída por assigned_to_user_id)
        // Nota: Verificar se a coluna "assigned_to" existe antes de remover
        try
        {
            Delete.Column("assigned_to").FromTable("tickets");
        }
        catch
        {
            // Coluna pode não existir, ignorar erro
        }

        Create.Index("idx_tickets_department")
            .OnTable("tickets")
            .OnColumn("department_id");

        Create.Index("idx_tickets_workflow_profile")
            .OnTable("tickets")
            .OnColumn("workflow_profile_id");

        Create.Index("idx_tickets_sla_expires")
            .OnTable("tickets")
            .OnColumn("sla_expires_at");

        Create.Index("idx_tickets_sla_breached")
            .OnTable("tickets")
            .OnColumn("sla_breached");
    }

    public override void Down()
    {
        // Remover índices
        Delete.Index("idx_tickets_sla_breached").OnTable("tickets");
        Delete.Index("idx_tickets_sla_expires").OnTable("tickets");
        Delete.Index("idx_tickets_workflow_profile").OnTable("tickets");
        Delete.Index("idx_tickets_department").OnTable("tickets");
        Delete.Index("idx_ticket_activity_logs_type").OnTable("ticket_activity_logs");
        Delete.Index("idx_ticket_activity_logs_created").OnTable("ticket_activity_logs");
        Delete.Index("idx_ticket_activity_logs_ticket").OnTable("ticket_activity_logs");
        Delete.Index("idx_workflow_profiles_client").OnTable("workflow_profiles");
        Delete.Index("idx_workflow_profiles_dept").OnTable("workflow_profiles");
        Delete.Index("idx_departments_is_active").OnTable("departments");
        Delete.Index("idx_departments_client_id").OnTable("departments");

        // Remover colunas de tickets
        Delete.Column("sla_breached").FromTable("tickets");
        Delete.Column("sla_expires_at").FromTable("tickets");
        Delete.Column("assigned_to_user_id").FromTable("tickets");
        Delete.Column("workflow_profile_id").FromTable("tickets");
        Delete.Column("department_id").FromTable("tickets");

        // Remover tabelas
        Delete.Table("ticket_activity_logs");
        Delete.Table("workflow_profiles");
        Delete.Table("departments");
    }
}
