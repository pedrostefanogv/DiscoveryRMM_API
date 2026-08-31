using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Permite execuções automáticas de automation tasks (policy-sync: immediate/recurring/
/// user-login/check-in) sem comando dispatchado pelo servidor:
/// - Torna command_id nullable em automation_execution_reports.
/// - Remove a FK/unique de command_id e cria unique parcial em (agent_id, command_id)
///   apenas para registros com command_id (execuções RunNow continuam únicas).
/// - Adiciona índice (task_id, created_at desc) para o endpoint de execuções por task.
/// </summary>
[Migration(20260831_146)]
public class M146_AutomationPolicyExecutions : Migration
{
    public override void Up()
    {
        Delete.Index("ux_automation_execution_reports_command").OnTable("automation_execution_reports");
        Delete.ForeignKey("fk_automation_execution_reports_command_id_agent_commands").OnTable("automation_execution_reports");

        Alter.Table("automation_execution_reports")
            .AlterColumn("command_id").AsGuid().Nullable();

        Create.Index("ux_automation_execution_reports_agent_command")
            .OnTable("automation_execution_reports")
            .OnColumn("agent_id").Ascending()
            .OnColumn("command_id").Ascending()
            .WithOptions().Unique();

        Create.Index("ix_automation_execution_reports_task_created")
            .OnTable("automation_execution_reports")
            .OnColumn("task_id").Ascending()
            .OnColumn("created_at").Descending();
    }

    public override void Down()
    {
        Delete.Index("ix_automation_execution_reports_task_created").OnTable("automation_execution_reports");
        Delete.Index("ux_automation_execution_reports_agent_command").OnTable("automation_execution_reports");

        Alter.Table("automation_execution_reports")
            .AlterColumn("command_id").AsGuid().NotNullable();

        Create.ForeignKey("fk_automation_execution_reports_command_id_agent_commands")
            .FromTable("automation_execution_reports").ForeignColumn("command_id")
            .ToTable("agent_commands").PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("ux_automation_execution_reports_command")
            .OnTable("automation_execution_reports")
            .OnColumn("command_id").Ascending()
            .WithOptions().Unique();
    }
}
