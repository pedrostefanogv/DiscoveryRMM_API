using FluentMigrator;

namespace Discovery.Migrations.Migrations;

/// <summary>
/// Opt-in por tarefa de automação: respeitar a janela de manutenção do agent
/// nas ações de PACOTE (install/upgrade/uninstall). Quando
/// <c>task_respect_maintenance_window = true</c>, o agent só executa a ação
/// dentro da janela efetiva do agente; fora dela a execução é adiada (NoOp
/// "deferred_to_window", sucesso — não ativa o circuit breaker).
///
/// Default false: tarefas antigas NÃO mudam de comportamento. O agente só
/// aplica o gate quando o campo vem como true no policy-sync.
/// </summary>
[Migration(20261231_256)]
public class M256_AddTaskRespectMaintenanceWindowToAutomationTasks : Migration
{
    private const string Table = "automation_task_definitions";

    public override void Up()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (!Schema.Table(Table).Column("task_respect_maintenance_window").Exists())
        {
            Alter.Table(Table).AddColumn("task_respect_maintenance_window").AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }

    public override void Down()
    {
        if (!Schema.Table(Table).Exists())
            return;

        if (Schema.Table(Table).Column("task_respect_maintenance_window").Exists())
            Delete.Column("task_respect_maintenance_window").FromTable(Table);
    }
}
